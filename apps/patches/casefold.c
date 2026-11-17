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
// Strategy: call through to libc first, and only for READS. When open/fopen
// fails with ENOENT (and the caller is not creating the file) the path is
// re-walked component-by-component, trying the exact spelling first and then
// the case-insensitive siblings, and the call is retried exactly once. A dead
// end backtracks to the case twin: s&box publishes compiled outputs into a
// lowercased tree, so the exact name can exist and still be the wrong branch
// (a lowercase `smarttrousers/` output directory shadowing `SmartTrousers/`,
// where the referenced FBX actually lives). Zero overhead on the hit path, and
// the tree on disk is never modified.
//
// The stat family (stat, lstat, access, statx) deliberately stays exact. The
// managed content builder asks "does this output exist?" and then renames it; a
// stat answered through the case twin makes that rename fail on the path that
// is really there, which broke every model publish. Reads still fall back, and
// that is where the compilers load FBX and generated-child .meta references.

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

// Join `prefix` and `name` into `out`. Kept separate because the prefix may
// alias the source when it is the same buffer; never snprintf over itself.
static int casefold_join( const char *prefix, const char *name, char *out, size_t size )
{
	int n;
	if ( prefix[0] == '\0' || ( prefix[0] == '.' && prefix[1] == '\0' ) )
		n = snprintf( out, size, "./%s", name );
	else if ( prefix[0] == '/' && prefix[1] == '\0' )
		n = snprintf( out, size, "/%s", name );
	else
		n = snprintf( out, size, "%s/%s", prefix, name );
	return n < 0 || (size_t)n >= size ? -1 : n;
}

// True when more path components follow `rest` (strtok_r's cursor), so a
// directory component can be treated differently from the last one.
static int casefold_has_more( const char *rest )
{
	if ( rest == NULL ) return 0;
	for ( ; *rest; rest++ )
		if ( *rest != '/' ) return 1;
	return 0;
}

// One linear pass over the components. The exact-first pass takes each
// component as written and only matches a sibling case-insensitively when the
// written spelling is missing. The twin pass prefers a sibling with a
// different spelling, which is what unshadows the case twin of a directory
// that exists but does not contain the rest of the path.
static int casefold_pass( const char *path, int exact_first, char *out )
{
	char work[PATH_MAX];
	size_t pathLen = strlen( path );
	if ( pathLen == 0 || pathLen >= sizeof( work ) )
	{
		errno = ENOENT;
		return -1;
	}
	memcpy( work, path, pathLen + 1 );

	char cur[PATH_MAX];
	if ( path[0] == '/' )
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
		if ( comp[0] == '\0' || ( comp[0] == '.' && comp[1] == '\0' ) )
			continue;

		char candidate[PATH_MAX];
		if ( casefold_join( cur, comp, candidate, sizeof( candidate ) ) < 0 )
		{
			errno = ENOENT;
			return -1;
		}

		struct stat st;
		int exactExists = real_lstat( candidate, &st ) == 0;

		// The last component keeps the exact spelling when it exists: a twin
		// directory may also hold a differently-cased copy of the same file,
		// and the path as written means that one. Every earlier component is
		// a directory on the way, so the retry may prefer its case twin.
		int last = !casefold_has_more( save );

		if ( !( last && exactExists ) && ( !exact_first || !exactExists ) )
		{
			DIR *dir = opendir( cur );
			if ( !dir )
			{
				errno = ENOENT;
				return -1;
			}

			char alt[NAME_MAX + 1];
			int haveAlt = 0;
			struct dirent *entry;
			while ( ( entry = readdir( dir ) ) != NULL )
			{
				if ( strcmp( entry->d_name, comp ) == 0 ) continue;
				if ( strcasecmp( entry->d_name, comp ) != 0 ) continue;

				size_t nameLen = strlen( entry->d_name );
				if ( nameLen <= NAME_MAX )
				{
					memcpy( alt, entry->d_name, nameLen + 1 );
					haveAlt = 1;
				}
				break;
			}
			closedir( dir );

			if ( haveAlt )
			{
				char twin[PATH_MAX];
				if ( casefold_join( cur, alt, twin, sizeof( twin ) ) < 0 )
				{
					errno = ENOENT;
					return -1;
				}

				if ( real_lstat( twin, &st ) == 0 )
					memcpy( candidate, twin, strlen( twin ) + 1 );
				else if ( !exactExists )
				{
					errno = ENOENT;
					return -1;
				}
			}
			else if ( !exactExists )
			{
				errno = ENOENT;
				return -1;
			}
		}

		size_t candidateLen = strlen( candidate );
		if ( candidateLen == 0 || candidateLen >= sizeof( cur ) )
		{
			errno = ENOENT;
			return -1;
		}
		memcpy( cur, candidate, candidateLen + 1 );
	}

	size_t curLen = strlen( cur );
	if ( curLen == 0 || curLen >= PATH_MAX )
	{
		errno = ENOENT;
		return -1;
	}
	memcpy( out, cur, curLen + 1 );
	return 0;
}

// Returns 0 and fills `out` on success, -1 with errno == ENOENT when no
// spelling of the path resolves.

static int casefold_resolve( const char *path, char *out )
{
	ensure_binds();
	// The walk only needs lstat; every caller already ensured the bindings,
	// but re-check: a still-NULL binding fails safe instead of crashing.
	if ( !real_lstat )
	{
		errno = ENOENT;
		return -1;
	}

	// Exact spelling first: the common fallback only has a wrongly-cased leaf.
	// A dead end is retried with the case twins, because s&box publishes
	// compiled outputs into a lowercased tree - the exact name can exist and
	// still be the wrong branch (a lowercase `smarttrousers/` output directory
	// shadowing `SmartTrousers/`, where the referenced FBX actually lives).
	if ( casefold_pass( path, 1, out ) != 0 && casefold_pass( path, 0, out ) != 0 )
	{
		errno = ENOENT;
		return -1;
	}

	if ( verbose() )
		fprintf( stderr, "[casefold] %s -> %s\n", path, out );
	return 0;
}



// ---------------------------------------------------------------------------
// Interposed calls: try libc first, fall back exactly once on ENOENT.
// Creates (O_CREAT/O_TMPFILE, write-mode fopen) never fall back, and the stat
// family is exact (see the strategy note at the top).
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
	return real_stat( path, buf );
}

int lstat( const char *path, struct stat *buf )
{
	ensure_binds();
	if ( !real_lstat ) { errno = ENOSYS; return -1; }
	return real_lstat( path, buf );
}

int access( const char *path, int mode )
{
	ensure_binds();
	if ( !real_access ) { errno = ENOSYS; return -1; }
	return real_access( path, mode );
}

int stat64( const char *path, struct stat64 *buf )
{
	ensure_binds();
	if ( !real_stat64 ) { errno = ENOSYS; return -1; }
	return real_stat64( path, buf );
}

int lstat64( const char *path, struct stat64 *buf )
{
	ensure_binds();
	if ( !real_lstat64 ) { errno = ENOSYS; return -1; }
	return real_lstat64( path, buf );
}

int faccessat( int dirfd, const char *path, int mode, int flags )
{
	ensure_binds();
	if ( !real_faccessat ) { errno = ENOSYS; return -1; }
	return real_faccessat( dirfd, path, mode, flags );
}

int statx( int dirfd, const char *path, int flags, unsigned int mask, struct statx *bufx )
{
	ensure_binds();
	if ( !real_statx ) { errno = ENOSYS; return -1; }
	return real_statx( dirfd, path, flags, mask, bufx );
}
