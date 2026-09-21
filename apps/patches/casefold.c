// casefold.c — LD_PRELOAD shim giving Linux s&box tools a case-insensitive
// fallback for asset references authored with the wrong filename case.
//
// Background: model/texture descriptors (e.g. citizen .vmdl files) committed
// with lowercase FBX paths resolve on Windows/macOS but fail on case-sensitive
// Linux filesystems, breaking contentbuilder/resourcecompiler and editor
// asset loads. The descriptors are distributed to every client, so they must
// not be edited locally to paper over it.
//
// Vendored here (apps/patches/) and built on demand into the ampersand cache
// dir; _common.sh appends it to LD_PRELOAD when SBOX_CASEFOLD=1, and Bootstrap
// exports it around Setup.sh so content builds inherit it. Opt-in via the
// "Casefold" toggle (off by default), override location with SBOX_CASEFOLD_SO,
// diagnostics with CASEFOLD_VERBOSE=1.
//
// Strategy: call through to libc first. Only when the call fails with ENOENT
// (and, for opens, the caller is not creating the file) the path is re-walked
// component-by-component, matching each directory entry case-insensitively,
// and the call is retried exactly once. Zero overhead on the hit path, and the
// tree on disk is never modified.

#define _GNU_SOURCE

#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

// ---------------------------------------------------------------------------
// libc bindings (resolved lazily via RTLD_NEXT - see ensure_binds)
// ---------------------------------------------------------------------------

static int (*real_open)( const char *path, int flags, ... );
static int (*real_open64)( const char *path, int flags, ... );
static int (*real_openat)( int dirfd, const char *path, int flags, ... );
static int (*real_openat64)( int dirfd, const char *path, int flags, ... );
static FILE *(*real_fopen)( const char *path, const char *mode );
static FILE *(*real_fopen64)( const char *path, const char *mode );
static int (*real_stat)( const char *path, struct stat *buf );
static int (*real_lstat)( const char *path, struct stat *buf );
// Large-file twins: current toolchains redirect stat()/lstat() to these
// (stat64@GLIBC_2.33), so tier0/contentbuilder/resourcecompiler only ever
// call the 64-bit spellings. Layout-identical to struct stat on 64-bit.
static int (*real_stat64)( const char *path, struct stat64 *buf );
static int (*real_lstat64)( const char *path, struct stat64 *buf );
static int (*real_access)( const char *path, int mode );
static int (*real_faccessat)( int dirfd, const char *path, int mode, int flags );
// statx: Qt (libQt5Core) stats through this, bypassing the stat family.
static int (*real_statx)( int dirfd, const char *path, int flags, unsigned int mask, struct statx *bufx );

// Resolve the real libc bindings on first use, not in a constructor.
// Other shared libraries run their constructors during _dl_init (e.g.
// libselinux calls access()) potentially before any constructor of ours
// would run, so eagerly-resolved pointers may still be NULL when an
// interposed function is first called - jumping through one is an instant
// SIGSEGV before main(). dlsym(RTLD_NEXT) here is safe in that context.
// Idempotent; concurrently racing threads store the same addresses.
static void ensure_binds( void )
{
	if ( !real_open ) real_open = dlsym( RTLD_NEXT, "open" );
	if ( !real_open64 ) real_open64 = dlsym( RTLD_NEXT, "open64" );
	if ( !real_openat ) real_openat = dlsym( RTLD_NEXT, "openat" );
	if ( !real_openat64 ) real_openat64 = dlsym( RTLD_NEXT, "openat64" );
	if ( !real_fopen ) real_fopen = dlsym( RTLD_NEXT, "fopen" );
	if ( !real_fopen64 ) real_fopen64 = dlsym( RTLD_NEXT, "fopen64" );
	if ( !real_stat ) real_stat = dlsym( RTLD_NEXT, "stat" );
	if ( !real_lstat ) real_lstat = dlsym( RTLD_NEXT, "lstat" );
	if ( !real_stat64 ) real_stat64 = dlsym( RTLD_NEXT, "stat64" );
	if ( !real_lstat64 ) real_lstat64 = dlsym( RTLD_NEXT, "lstat64" );
	if ( !real_access ) real_access = dlsym( RTLD_NEXT, "access" );
	if ( !real_faccessat ) real_faccessat = dlsym( RTLD_NEXT, "faccessat" );
	if ( !real_statx ) real_statx = dlsym( RTLD_NEXT, "statx" );
}

static int verbose( void )
{
	static int cached = -1;
	if ( cached < 0 )
		cached = getenv( "CASEFOLD_VERBOSE" ) != NULL ? 1 : 0;
	return cached;
}

// ---------------------------------------------------------------------------
// Case-insensitive path resolution
// ---------------------------------------------------------------------------

// Re-walk `path`, matching each component case-insensitively when the exact
// name is absent. Relative paths resolve against the process cwd (which is
// what AT_FDCWD callers mean). Returns 0 and fills `out` on success, -1 with
// errno == ENOENT when any component has no case-insensitive match.
static int casefold_resolve( const char *path, char *out )
{
	ensure_binds();
	// resolve itself only needs real_lstat; every caller already ensured,
	// but re-check: a still-NULL binding fails safe instead of crashing.
	if ( !real_lstat )
	{
		errno = ENOENT;
		return -1;
	}
	char work[PATH_MAX];
	size_t pathLen = strlen( path );
	if ( pathLen == 0 || pathLen >= sizeof( work ) )
	{
		errno = ENOENT;
		return -1;
	}
	memcpy( work, path, pathLen + 1 );

	// `cur` accumulates the resolved prefix.
	char cur[PATH_MAX];
	if ( work[0] == '/' )
	{
		cur[0] = '/';
		cur[1] = '\0';
	}
	else
	{
		cur[0] = '.';
		cur[1] = '\0';
	}

	for ( char *save = NULL, *comp = strtok_r( work, "/", &save );
		comp != NULL;
		comp = strtok_r( NULL, "/", &save ) )
	{
		if ( comp[0] == '\0' || (comp[0] == '.' && comp[1] == '\0') )
			continue;

		char candidate[PATH_MAX];
		int need = snprintf( candidate, sizeof( candidate ), "%s/%s",
			cur[0] == '\0' ? "." : cur, comp );
		if ( need < 0 || (size_t)need >= sizeof( candidate ) )
		{
			errno = ENOENT;
			return -1;
		}

		struct stat st;
		if ( real_lstat( candidate, &st ) == 0 )
		{
			memcpy( cur, candidate, (size_t)need + 1 );
			continue;
		}

		// Exact name missing: scan the parent directory case-insensitively.
		const char *parent = cur[0] == '\0' ? "." : cur;
		DIR *dir = opendir( parent );
		if ( !dir )
		{
			errno = ENOENT;
			return -1;
		}

		const char *match = NULL;
		// Stack buffer, not static: resourcecompiler resolves assets on
		// worker threads and a shared buffer would race between concurrent
		// fallbacks. `match` is consumed below before this function returns.
		char matched[NAME_MAX + 1];
		struct dirent *entry;
		while ( (entry = readdir( dir )) != NULL )
		{
			if ( strcasecmp( entry->d_name, comp ) == 0 )
			{
				size_t nameLen = strlen( entry->d_name );
				if ( nameLen <= NAME_MAX )
				{
					memcpy( matched, entry->d_name, nameLen + 1 );
					match = matched;
				}
				break;
			}
		}
		closedir( dir );

		if ( !match )
		{
			errno = ENOENT;
			return -1;
		}

		// Join into a scratch buffer: parent aliases cur, and snprintf
		// with overlapping source/destination is undefined behavior that
		// truncates on current glibc (yields "/<match>", dropping the prefix).
		char next[PATH_MAX];
		need = snprintf( next, sizeof( next ), "%s/%s",
			parent[0] == '\0' ? "." : parent, match );
		if ( need < 0 || (size_t)need >= sizeof( next ) )
		{
			errno = ENOENT;
			return -1;
		}
		memcpy( cur, next, (size_t)need + 1 );
	}

	size_t curLen = strlen( cur );
	if ( curLen == 0 || curLen >= PATH_MAX )
	{
		errno = ENOENT;
		return -1;
	}
	memcpy( out, cur, curLen + 1 );

	if ( verbose() )
		fprintf( stderr, "[casefold] %s -> %s\n", path, out );
	return 0;
}

// ---------------------------------------------------------------------------
// Interposed calls: try libc first, fall back exactly once on ENOENT.
// Creates (O_CREAT/O_TMPFILE, write-mode fopen) never fall back.
// ---------------------------------------------------------------------------

int open( const char *path, int flags, ... )
{
	ensure_binds();
	if ( !real_open ) { errno = ENOSYS; return -1; }
	mode_t mode = 0;
	if ( flags & (O_CREAT | O_TMPFILE) )
	{
		va_list ap;
		va_start( ap, flags );
		mode = va_arg( ap, mode_t );
		va_end( ap );
		return real_open( path, flags, mode );
	}

	int fd = real_open( path, flags );
	if ( fd < 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fd = real_open( resolved, flags );
		else
			errno = ENOENT;
	}
	return fd;
}

int open64( const char *path, int flags, ... )
{
	ensure_binds();
	if ( !real_open64 ) { errno = ENOSYS; return -1; }
	mode_t mode = 0;
	if ( flags & (O_CREAT | O_TMPFILE) )
	{
		va_list ap;
		va_start( ap, flags );
		mode = va_arg( ap, mode_t );
		va_end( ap );
		return real_open64( path, flags, mode );
	}

	int fd = real_open64( path, flags );
	if ( fd < 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fd = real_open64( resolved, flags );
		else
			errno = ENOENT;
	}
	return fd;
}

static int openat_may_fallback( int dirfd, const char *path )
{
	return path[0] == '/' || dirfd == AT_FDCWD;
}

int openat( int dirfd, const char *path, int flags, ... )
{
	ensure_binds();
	if ( !real_openat ) { errno = ENOSYS; return -1; }
	mode_t mode = 0;
	if ( flags & (O_CREAT | O_TMPFILE) )
	{
		va_list ap;
		va_start( ap, flags );
		mode = va_arg( ap, mode_t );
		va_end( ap );
		return real_openat( dirfd, path, flags, mode );
	}

	int fd = real_openat( dirfd, path, flags );
	if ( fd < 0 && errno == ENOENT && openat_may_fallback( dirfd, path ) )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fd = real_openat( dirfd, resolved, flags );
		else
			errno = ENOENT;
	}
	return fd;
}

int openat64( int dirfd, const char *path, int flags, ... )
{
	ensure_binds();
	if ( !real_openat64 ) { errno = ENOSYS; return -1; }
	mode_t mode = 0;
	if ( flags & (O_CREAT | O_TMPFILE) )
	{
		va_list ap;
		va_start( ap, flags );
		mode = va_arg( ap, mode_t );
		va_end( ap );
		return real_openat64( dirfd, path, flags, mode );
	}

	int fd = real_openat64( dirfd, path, flags );
	if ( fd < 0 && errno == ENOENT && openat_may_fallback( dirfd, path ) )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fd = real_openat64( dirfd, resolved, flags );
		else
			errno = ENOENT;
	}
	return fd;
}

FILE *fopen( const char *path, const char *mode )
{
	ensure_binds();
	if ( !real_fopen ) { errno = ENOSYS; return NULL; }
	FILE *fp = real_fopen( path, mode );
	// Read modes never create; write/append modes do (or truncate), and must
	// keep exact-path semantics so outputs land where the caller expects.
	if ( !fp && errno == ENOENT && mode[0] == 'r' )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fp = real_fopen( resolved, mode );
		else
			errno = ENOENT;
	}
	return fp;
}

FILE *fopen64( const char *path, const char *mode )
{
	ensure_binds();
	if ( !real_fopen64 ) { errno = ENOSYS; return NULL; }
	FILE *fp = real_fopen64( path, mode );
	if ( !fp && errno == ENOENT && mode[0] == 'r' )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			fp = real_fopen64( resolved, mode );
		else
			errno = ENOENT;
	}
	return fp;
}

int stat( const char *path, struct stat *buf )
{
	ensure_binds();
	if ( !real_stat ) { errno = ENOSYS; return -1; }
	int rc = real_stat( path, buf );
	if ( rc != 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_stat( resolved, buf );
		else
			errno = ENOENT;
	}
	return rc;
}

int lstat( const char *path, struct stat *buf )
{
	ensure_binds();
	if ( !real_lstat ) { errno = ENOSYS; return -1; }
	int rc = real_lstat( path, buf );
	if ( rc != 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_lstat( resolved, buf );
		else
			errno = ENOENT;
	}
	return rc;
}

int access( const char *path, int mode )
{
	ensure_binds();
	if ( !real_access ) { errno = ENOSYS; return -1; }
	int rc = real_access( path, mode );
	if ( rc != 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_access( resolved, mode );
		else
			errno = ENOENT;
	}
	return rc;
}

int stat64( const char *path, struct stat64 *buf )
{
	ensure_binds();
	if ( !real_stat64 ) { errno = ENOSYS; return -1; }
	int rc = real_stat64( path, buf );
	if ( rc != 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_stat64( resolved, buf );
		else
			errno = ENOENT;
	}
	return rc;
}

int lstat64( const char *path, struct stat64 *buf )
{
	ensure_binds();
	if ( !real_lstat64 ) { errno = ENOSYS; return -1; }
	int rc = real_lstat64( path, buf );
	if ( rc != 0 && errno == ENOENT )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_lstat64( resolved, buf );
		else
			errno = ENOENT;
	}
	return rc;
}

int faccessat( int dirfd, const char *path, int mode, int flags )
{
	ensure_binds();
	if ( !real_faccessat ) { errno = ENOSYS; return -1; }
	int rc = real_faccessat( dirfd, path, mode, flags );
	if ( rc != 0 && errno == ENOENT && openat_may_fallback( dirfd, path ) )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_faccessat( dirfd, resolved, mode, flags );
		else
			errno = ENOENT;
	}
	return rc;
}

int statx( int dirfd, const char *path, int flags, unsigned int mask, struct statx *bufx )
{
	ensure_binds();
	if ( !real_statx ) { errno = ENOSYS; return -1; }
	int rc = real_statx( dirfd, path, flags, mask, bufx );
	if ( rc != 0 && errno == ENOENT && openat_may_fallback( dirfd, path ) )
	{
		char resolved[PATH_MAX];
		if ( casefold_resolve( path, resolved ) == 0 )
			rc = real_statx( dirfd, resolved, flags, mask, bufx );
		else
			errno = ENOENT;
	}
	return rc;
}
