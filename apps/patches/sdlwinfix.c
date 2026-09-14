// VENDORED into ampersand (apps/patches/) from sbox-public @ 977f2d80f26db25082c149c57f8297e59942fd99
// ("Consolidate SDL embed shim"). The engine checkout deleted bootstrap-linux/
// in the dev-env realign; ampersand's SDL embed fix toggle builds this with:
//   gcc -D_GNU_SOURCE -shared -fPIC -O1 -o libsdlwinfix.so sdlwinfix.c -ldl -lX11
// TRANSIENT: delete this directory once Facepunch fixes the native embedding.
// libsdlwinfix — corrective shim for the SDL foreign-window embed defect.
//
// Qt embeds the SDL X window as a foreign child without forwarding geometry,
// so SDL_GetWindowSize/Position answer with the frozen creation values and the
// engine lays out against a stale origin. When the window is an embedded X
// child (parent != root) the shim answers from the live X parent geometry.
// Separately, SDL delivers the same physical click to both the embedded play
// view and the never-queried top-level editor window with different coords;
// the shift is the exact difference of the two windows' screen origins
// (XTranslateCoordinates to root), recomputed on geometry moves and session
// switches, and the twin's event/state coords are rewritten into play-view
// space. Top-level windows and every failure path pass through untouched
// (fail open to real values).
//
// SBOX_SDLWINFIX unset/0/empty = dormant passthrough, no logging.
// "observe" = resolve and log topology, fake nothing. Anything else ("1",
// "on", ...) = correct. Log goes to SDLWINFIX_LOG or logs/sdlwinfix.log;
// correct mode logs only create/topo/fail/move/xvec plus one summary line.
//
// Build: gcc -shared -fPIC -O2 -o libsdlwinfix.so sdlwinfix.c -lX11 -lpthread -ldl
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef void SDL_Window;
typedef uint32_t SDL_PropertiesID;

#define TITLE_LEN 64
#define MAX_WINDOWS 32

typedef struct
{
	int known;
	SDL_Window *ptr;
	unsigned id;
	char title[TITLE_LEN];
	// Last known-good parent geometry (move detection).
	int have_parent;
	int ppx;
	int ppy;
	int ppw;
	int pph;
	int topo_done;
	int lfail; // last fail reason logged (change-gated, 0=ok)
	unsigned nq; // query count: identifies the engine's sizing window
	unsigned long xid; // live X xid (for screen-origin translation)
} WindowEntry;

static WindowEntry windows[MAX_WINDOWS];

// Fail reasons (change-gated fail lines).
#define FR_OK 0
#define FR_SYM 1 // dlsym failed
#define FR_PROPS 2 // no SDL properties
#define FR_XID 3 // no X11 xid property
#define FR_DPY 4 // no X display (connection down)
#define FR_QUERY 5 // XQueryTree failed
#define FR_GEO 6 // XGetGeometry failed

static SDL_Window *(*real_CreateWindowWithProperties)( SDL_PropertiesID ) = 0;
static unsigned (*real_GetWindowID)( SDL_Window * ) = 0;
static const char *(*real_GetWindowTitle)( SDL_Window * ) = 0;
static int (*real_GetWindowSize)( SDL_Window *, int *, int * ) = 0;
static int (*real_GetWindowPosition)( SDL_Window *, int *, int * ) = 0;
static SDL_PropertiesID (*real_GetWindowProperties)( SDL_Window * ) = 0;
static long long (*real_GetNumberProperty)( SDL_PropertiesID, const char *, long long ) = 0;
static int (*real_PollEvent)( void * ) = 0;
static SDL_Window *(*real_GetWindowFromID)( unsigned ) = 0;
static SDL_Window *(*real_GetMouseFocus)( void ) = 0;
static unsigned (*real_GetMouseState)( float *, float * ) = 0;

static unsigned long n_create = 0;
static unsigned long n_getsize = 0;
static unsigned long n_getposition = 0;
static unsigned long n_faked = 0;
static unsigned long n_moved = 0;
static unsigned long moved_seq = 0; // bumped on every embed geometry change; invalidates ghost vectors
static unsigned long n_xvec = 0;
static unsigned long n_remap_ev = 0;
static unsigned long n_remap_state = 0;

static int log_fd = -1;
static int log_failed = 0;

// Persistent X connection. One Display shared by all our queries on all
// threads, guarded by our own mutex (SDL uses its own connection, so there
// is no lock-domain interaction). Reconnected lazily; reconnect attempts
// throttled to 1 Hz so a dead X never becomes a syscall storm.
static Display *g_dpy = 0;
static pthread_mutex_t g_dpy_mutex = PTHREAD_MUTEX_INITIALIZER;
static long long g_dpy_fail_ms = 0;

// SBOX_SDLWINFIX unset/0/empty = dormant passthrough, no logging.
// "observe" = log only. Anything else ("1", "on", ...) = correct.
static int mode_cached = -1;
static int mode( void )
{
	if ( mode_cached >= 0 )
	{
		return mode_cached;
	}
	const char *v = getenv( "SBOX_SDLWINFIX" );
	int m = 0;
	if ( v && v[0] )
	{
		if ( strcmp( v, "0" ) != 0 && strcmp( v, "off" ) != 0 && strcmp( v, "false" ) != 0 )
		{
			m = ( strcmp( v, "observe" ) == 0 || strcmp( v, "log" ) == 0 ) ? 1 : 2;
		}
	}
	mode_cached = m;
	return m;
}

static long long now_ms( void )
{
	struct timespec ts;
	clock_gettime( CLOCK_REALTIME, &ts );
	return (long long)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;
}

static void log_open( void )
{
	if ( log_fd >= 0 || log_failed )
	{
		return;
	}
	const char *path = getenv( "SDLWINFIX_LOG" );
	if ( !path || !path[0] )
	{
		path = "logs/sdlwinfix.log";
	}
	log_fd = open( path, O_WRONLY | O_CREAT | O_APPEND, 0644 );
	if ( log_fd < 0 )
	{
		log_failed = 1;
	}
}

static void emit( const char *buf, int len )
{
	if ( mode() == 0 )
	{
		return;
	}
	log_open();
	if ( log_fd < 0 || len <= 0 )
	{
		return;
	}
	(void)!write( log_fd, buf, (size_t)len );
}

static void emitf( const char *fmt, ... )
{
	char buf[384];
	va_list ap;
	va_start( ap, fmt );
	int len = vsnprintf( buf, sizeof( buf ), fmt, ap );
	va_end( ap );
	if ( len <= 0 )
	{
		return;
	}
	if ( len >= (int)sizeof( buf ) )
	{
		len = (int)sizeof( buf ) - 1;
	}
	emit( buf, len );
}

static void *resolve( void **slot, const char *name )
{
	if ( !*slot )
	{
		*slot = dlsym( RTLD_NEXT, name );
	}
	return *slot;
}

// X connection helpers; call with g_dpy_mutex held. x_display_locked opens
// lazily with the 1 Hz reconnect throttle; x_drop_locked discards the wire
// on X errors (stale xid, server hiccup) so the next query reconnects
// instead of failing forever on a dead connection.
static int x_display_locked( void )
{
	if ( g_dpy )
	{
		return 1;
	}
	long long now = now_ms();
	if ( now - g_dpy_fail_ms < 1000 )
	{
		return 0;
	}
	g_dpy = XOpenDisplay( NULL );
	if ( !g_dpy )
	{
		g_dpy_fail_ms = now;
		return 0;
	}
	return 1;
}

static void x_drop_locked( void )
{
	if ( g_dpy )
	{
		XCloseDisplay( g_dpy );
		g_dpy = 0;
	}
}

static WindowEntry *track_window( SDL_Window *window )
{
	int free_slot = -1;
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( windows[i].known && windows[i].ptr == window )
		{
			if ( real_GetWindowID )
			{
				unsigned fresh = real_GetWindowID( window );
				if ( fresh != 0 && fresh != windows[i].id )
				{
					// Same pointer, new window incarnation: reset, re-init below.
					SDL_Window *keep = windows[i].ptr;
					memset( &windows[i], 0, sizeof( windows[i] ) );
					windows[i].ptr = keep;
					free_slot = i;
					break;
				}
			}
			return &windows[i];
		}
		if ( !windows[i].known && free_slot < 0 )
		{
			free_slot = i;
		}
	}
	if ( free_slot < 0 )
	{
		return 0;
	}
	WindowEntry *e = &windows[free_slot];
	memset( e, 0, sizeof( *e ) );
	e->ptr = window;
	e->known = 1;
	if ( real_GetWindowID )
	{
		e->id = real_GetWindowID( window );
	}
	if ( real_GetWindowTitle )
	{
		const char *t = real_GetWindowTitle( window );
		if ( t )
		{
			strncpy( e->title, t, TITLE_LEN - 1 );
			e->title[TITLE_LEN - 1] = '\0';
		}
	}
	return e;
}

// Resolve the X geometry behind an SDL window over the persistent connection.
// Returns 1 with parent geometry when embedded (parent != root), 0 for a
// top-level window (pass through), or -reason on any failure (fail open).
static int x11_parent( SDL_Window *window, int *px, int *py, int *pw, int *ph,
	unsigned long *xid_out, unsigned long *parent_out, int *wx, int *wy, int *ww, int *wh )
{
	if ( !resolve( (void **)&real_GetWindowProperties, "SDL_GetWindowProperties" ) ||
		!resolve( (void **)&real_GetNumberProperty, "SDL_GetNumberProperty" ) )
	{
		return -FR_SYM;
	}
	SDL_PropertiesID props = real_GetWindowProperties( window );
	if ( !props )
	{
		return -FR_PROPS;
	}
	unsigned long xid = (unsigned long)real_GetNumberProperty( props, "SDL.window.x11.window", 0 );
	if ( !xid )
	{
		return -FR_XID;
	}
	pthread_mutex_lock( &g_dpy_mutex );
	Window root = 0;
	Window parent = 0;
	int ret = 0;
	if ( !x_display_locked() )
	{
		ret = -FR_DPY;
	}
	else
	{
		Window *children = 0;
		unsigned int nchildren = 0;
		if ( XQueryTree( g_dpy, (Window)xid, &root, &parent, &children, &nchildren ) )
		{
			if ( children )
			{
				XFree( children );
			}
			if ( parent != 0 && parent != root )
			{
				int x = 0;
				int y = 0;
				unsigned int w = 0;
				unsigned int h = 0;
				unsigned int bw = 0;
				unsigned int depth = 0;
				Window wroot = 0;
				if ( XGetGeometry( g_dpy, parent, &wroot, &x, &y, &w, &h, &bw, &depth ) )
				{
					*px = x;
					*py = y;
					*pw = (int)w;
					*ph = (int)h;
					ret = 1;
					// Own geometry for the topo line (best effort).
					if ( wx && XGetGeometry( g_dpy, (Window)xid, &wroot, &x, &y, &w, &h, &bw, &depth ) )
					{
						*wx = x;
						*wy = y;
						*ww = (int)w;
						*wh = (int)h;
					}
				}
				else
				{
					ret = -FR_GEO;
				}
			}
			else
			{
				ret = 0;
			}
		}
		else
		{
			ret = -FR_QUERY;
		}
	}
	if ( ret < 0 )
	{
		x_drop_locked();
	}
	if ( xid_out )
	{
		*xid_out = xid;
	}
	if ( parent_out )
	{
		*parent_out = (unsigned long)parent;
	}
	pthread_mutex_unlock( &g_dpy_mutex );
	return ret;
}

static void log_topo_once( WindowEntry *e, unsigned long xid, unsigned long parent,
	int wx, int wy, int ww, int wh, int px, int py, int pw, int ph )
{
	if ( e->topo_done )
	{
		return;
	}
	e->topo_done = 1;
	emitf( "t=%lld topo id=%u xid=%lu parent=%lu wingeo=%d,%d,%dx%d pargeo=%d,%d,%dx%d\n",
		now_ms(), (unsigned)e->id, xid, parent, wx, wy, ww, wh, px, py, pw, ph );
}

// Find a tracked window by SDL window id.
static WindowEntry *entry_for_id( unsigned wid )
{
	if ( wid == 0 )
	{
		return 0;
	}
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( windows[i].known && windows[i].id == wid )
		{
			return &windows[i];
		}
	}
	return 0;
}

static const char *reason_name( int r )
{
	switch ( r )
	{
	case FR_OK: return "ok";
	case FR_SYM: return "sym";
	case FR_PROPS: return "props";
	case FR_XID: return "xid";
	case FR_DPY: return "dpy";
	case FR_QUERY: return "query";
	case FR_GEO: return "geo";
	default: return "?";
	}
}

// Change-gated fail/recovery line: first failure, reason changes, recovery.
static void log_fail( WindowEntry *e, int reason )
{
	if ( e->lfail == reason )
	{
		return;
	}
	e->lfail = reason;
	emitf( "t=%lld fail id=%u \"%s\" reason=%s\n",
		now_ms(), (unsigned)e->id, e->title, reason_name( reason ) );
}

// Geometry-change detector: updates last-known parent geometry, reports change.
// moved_seq keys ghost-vector invalidation off this - the move line is secondary.
static int note_parent_geo( WindowEntry *e, int px, int py, int pw, int ph,
	int *ox, int *oy, int *ow, int *oh )
{
	int changed = 0;
	if ( e->have_parent )
	{
		changed = ( e->ppx != px || e->ppy != py || e->ppw != pw || e->pph != ph );
		*ox = e->ppx;
		*oy = e->ppy;
		*ow = e->ppw;
		*oh = e->pph;
	}
	e->have_parent = 1;
	e->ppx = px;
	e->ppy = py;
	e->ppw = pw;
	e->pph = ph;
	return changed;
}

// Shared query path for size/position: resolve parent geometry in observe and
// correct modes (so observe also proves lookup reliability), fake answers in
// correct mode only.
static int query_parent( WindowEntry *e, int *px, int *py, int *pw, int *ph,
	unsigned long *xid, unsigned long *parent, int *wx, int *wy, int *ww, int *wh )
{
	int m = mode();
	if ( m == 0 )
	{
		return 0;
	}
	int r = x11_parent( e->ptr, px, py, pw, ph, xid, parent, wx, wy, ww, wh );
	if ( r == 1 )
	{
		e->xid = *xid;
		log_topo_once( e, *xid, *parent, *wx, *wy, *ww, *wh, *px, *py, *pw, *ph );
		log_fail( e, FR_OK );
		int ox = 0;
		int oy = 0;
		int ow = 0;
		int oh = 0;
		if ( note_parent_geo( e, *px, *py, *pw, *ph, &ox, &oy, &ow, &oh ) )
		{
			n_moved++;
			moved_seq++;
			emitf( "t=%lld move id=%u \"%s\" win %d,%d,%dx%d par %d,%d,%dx%d -> %d,%d,%dx%d\n",
				now_ms(), (unsigned)e->id, e->title,
				*wx, *wy, *ww, *wh, ox, oy, ow, oh, *px, *py, *pw, *ph );
		}
		return ( m == 2 ) ? 1 : 0;
	}
	if ( r < 0 )
	{
		log_fail( e, -r );
	}
	return 0;
}

// ---- interposed functions ----

SDL_Window *SDL_CreateWindowWithProperties( SDL_PropertiesID props )
{
	resolve( (void **)&real_CreateWindowWithProperties, "SDL_CreateWindowWithProperties" );
	resolve( (void **)&real_GetWindowID, "SDL_GetWindowID" );
	resolve( (void **)&real_GetWindowTitle, "SDL_GetWindowTitle" );
	n_create++;
	if ( !real_CreateWindowWithProperties )
	{
		return 0;
	}
	SDL_Window *w = real_CreateWindowWithProperties( props );
	if ( !w )
	{
		return w;
	}
	WindowEntry *e = track_window( w );
	if ( e )
	{
		emitf( "t=%lld create ptr=%p id=%u \"%s\"\n", now_ms(), (void *)w, (unsigned)e->id, e->title );
	}
	return w;
}

int SDL_GetWindowSize( SDL_Window *window, int *rw, int *rh )
{
	if ( !resolve( (void **)&real_GetWindowSize, "SDL_GetWindowSize" ) )
	{
		return -1;
	}
	n_getsize++;
	int rc = real_GetWindowSize( window, rw, rh );
	if ( !window || rc < 0 )
	{
		return rc;
	}
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	e->nq++;
	int px = 0;
	int py = 0;
	int pw = 0;
	int ph = 0;
	unsigned long xid = 0;
	unsigned long parent = 0;
	int wx = 0;
	int wy = 0;
	int ww = 0;
	int wh = 0;
	if ( query_parent( e, &px, &py, &pw, &ph, &xid, &parent, &wx, &wy, &ww, &wh ) )
	{
		if ( pw >= 1 && ph >= 1 )
		{
			if ( rw )
			{
				*rw = pw;
			}
			if ( rh )
			{
				*rh = ph;
			}
			n_faked++;
		}
	}
	return rc;
}

int SDL_GetWindowPosition( SDL_Window *window, int *rx, int *ry )
{
	if ( !resolve( (void **)&real_GetWindowPosition, "SDL_GetWindowPosition" ) )
	{
		return -1;
	}
	n_getposition++;
	int rc = real_GetWindowPosition( window, rx, ry );
	if ( !window || rc < 0 )
	{
		return rc;
	}
	WindowEntry *e = track_window( window );
	if ( !e )
	{
		return rc;
	}
	e->nq++;
	int px = 0;
	int py = 0;
	int pw = 0;
	int ph = 0;
	unsigned long xid = 0;
	unsigned long parent = 0;
	int wx = 0;
	int wy = 0;
	int ww = 0;
	int wh = 0;
	if ( query_parent( e, &px, &py, &pw, &ph, &xid, &parent, &wx, &wy, &ww, &wh ) )
	{
		if ( rx )
		{
			*rx = px;
		}
		if ( ry )
		{
			*ry = py;
		}
		n_faked++;
	}
	return rc;
}

// Most-queried live tracked window: the one the engine sizes/cursors from.
// The liveness gate matters across play sessions: the dead view keeps the
// highest all-time query count, but GetWindowFromID no longer resolves it.
static WindowEntry *main_entry( void )
{
	resolve( (void **)&real_GetWindowFromID, "SDL_GetWindowFromID" );
	WindowEntry *best = 0;
	for ( int i = 0; i < MAX_WINDOWS; i++ )
	{
		if ( !windows[i].known || windows[i].nq == 0 )
		{
			continue;
		}
		if ( real_GetWindowFromID && real_GetWindowFromID( windows[i].id ) != windows[i].ptr )
		{
			continue; // destroyed window: most queries ever, but dead
		}
		if ( !best || windows[i].nq > best->nq )
		{
			best = &windows[i];
		}
	}
	return best;
}

// Ghost-twin unification. A never-queried window echoing mouse events is a
// ghost twin living in a shifted space; the shift is the exact difference of
// the two windows' screen origins (XTranslateCoordinates to root). The vector
// stores main-minus-ghost (matching the observable twin delta); converting
// ghost->main subtracts it. Recomputed whenever the embed geometry moves
// (moved_seq) or the main window switches sessions. Correct mode only.
#define MAX_GHOSTS 4
static unsigned g_wid[MAX_GHOSTS] = { 0, 0, 0, 0 };
static float g_vx[MAX_GHOSTS] = { 0, 0, 0, 0 };
static float g_vy[MAX_GHOSTS] = { 0, 0, 0, 0 };
static int g_ok[MAX_GHOSTS] = { 0, 0, 0, 0 };
static unsigned g_main[MAX_GHOSTS] = { 0, 0, 0, 0 };
static unsigned long g_seq[MAX_GHOSTS] = { 0, 0, 0, 0 };

static int ghost_slot( unsigned wid )
{
	for ( int k = 0; k < MAX_GHOSTS; k++ )
	{
		if ( g_wid[k] == wid )
		{
			return k;
		}
	}
	for ( int k = 0; k < MAX_GHOSTS; k++ )
	{
		if ( g_wid[k] == 0 )
		{
			g_wid[k] = wid;
			return k;
		}
	}
	return -1;
}

static int screen_origin( unsigned long xid, int *sx, int *sy )
{
	Window child = 0;
	int x = 0;
	int y = 0;
	if ( !XTranslateCoordinates( g_dpy, (Window)xid, DefaultRootWindow( g_dpy ), 0, 0, &x, &y, &child ) )
	{
		return 0;
	}
	*sx = x;
	*sy = y;
	return 1;
}

static void xvec_recompute( unsigned wid )
{
	int slot = ghost_slot( wid );
	if ( slot < 0 )
	{
		return;
	}
	WindowEntry *main = main_entry();
	if ( !main || !main->xid )
	{
		return;
	}
	if ( g_ok[slot] && g_seq[slot] == moved_seq && g_main[slot] == main->id )
	{
		return;
	}
	if ( !resolve( (void **)&real_GetWindowFromID, "SDL_GetWindowFromID" ) ||
		!resolve( (void **)&real_GetWindowProperties, "SDL_GetWindowProperties" ) ||
		!resolve( (void **)&real_GetNumberProperty, "SDL_GetNumberProperty" ) )
	{
		return;
	}
	SDL_Window *gp = real_GetWindowFromID( wid );
	if ( !gp )
	{
		return;
	}
	SDL_PropertiesID props = real_GetWindowProperties( gp );
	if ( !props )
	{
		return;
	}
	unsigned long gxid = (unsigned long)real_GetNumberProperty( props, "SDL.window.x11.window", 0 );
	if ( !gxid )
	{
		return;
	}
	int msx = 0;
	int msy = 0;
	int gsx = 0;
	int gsy = 0;
	pthread_mutex_lock( &g_dpy_mutex );
	int ok = x_display_locked() &&
		screen_origin( main->xid, &msx, &msy ) &&
		screen_origin( gxid, &gsx, &gsy );
	pthread_mutex_unlock( &g_dpy_mutex );
	if ( !ok )
	{
		return;
	}
	float vx = (float)( msx - gsx );
	float vy = (float)( msy - gsy );
	if ( vx > 5000.0f || vx < -5000.0f || vy > 5000.0f || vy < -5000.0f )
	{
		return;
	}
	g_seq[slot] = moved_seq;
	g_main[slot] = main->id;
	if ( !g_ok[slot] || vx - g_vx[slot] > 0.5f || g_vx[slot] - vx > 0.5f ||
		vy - g_vy[slot] > 0.5f || g_vy[slot] - vy > 0.5f )
	{
		g_vx[slot] = vx;
		g_vy[slot] = vy;
		g_ok[slot] = 1;
		n_xvec++;
		emitf( "t=%lld xvec ghost=%u main=%u vec=%.1f,%.1f\n", now_ms(), wid, main->id, vx, vy );
	}
}

// Mouse event coords (SDL3 motion/button events share the prefix type@0 u32,
// windowID@16 u32, x@28 f32, y@32 f32), validated: finite and sane, else
// ignored so a misparsed layout can never shift the cursor.
typedef struct
{
	unsigned wid;
	float x;
	float y;
	int ok;
} MouseCoords;

static MouseCoords parse_coords( void *ev )
{
	MouseCoords c = { 0, 0.0f, 0.0f, 0 };
	memcpy( &c.wid, (char *)ev + 16, sizeof( c.wid ) );
	memcpy( &c.x, (char *)ev + 28, sizeof( c.x ) );
	memcpy( &c.y, (char *)ev + 32, sizeof( c.y ) );
	c.ok = ( c.x == c.x && c.y == c.y &&
		c.x <= 1e6f && c.x >= -1e6f && c.y <= 1e6f && c.y >= -1e6f );
	return c;
}

// Rewrite a ghost twin's coords into main space (correct mode only).
// Queried windows pass through untouched.
static void xremap( void *ev, MouseCoords *c )
{
	if ( !c->ok )
	{
		return;
	}
	WindowEntry *te = entry_for_id( c->wid );
	if ( te && te->nq > 0 )
	{
		return;
	}
	int slot = ghost_slot( c->wid );
	if ( slot < 0 )
	{
		return;
	}
	xvec_recompute( c->wid );
	if ( !g_ok[slot] || mode() != 2 )
	{
		return;
	}
	float nx = c->x - g_vx[slot];
	float ny = c->y - g_vy[slot];
	memcpy( (char *)ev + 28, &nx, 4 );
	memcpy( (char *)ev + 32, &ny, 4 );
	n_remap_ev++;
}

// SDL3 event ids: mouse motion 0x400, button down/up 0x401/0x402.
// Only the type field (offset 0) is read here; coords via parse_coords.
int SDL_PollEvent( void *ev )
{
	if ( !resolve( (void **)&real_PollEvent, "SDL_PollEvent" ) )
	{
		return 0;
	}
	int rc = real_PollEvent( ev );
	if ( mode() == 0 )
	{
		return rc;
	}
	if ( rc != 0 && ev )
	{
		unsigned type = *(volatile unsigned *)ev;
		if ( type == 0x400 || type == 0x401 || type == 0x402 )
		{
			MouseCoords c = parse_coords( ev );
			xremap( ev, &c );
		}
	}
	return rc;
}

// SDL_GetMouseState reports coords relative to the mouse-focus window.
// When focus sits on the ghost twin, translate into main space (correct
// mode only). Deltas are translation-invariant - relative mode untouched.
unsigned SDL_GetMouseState( float *x, float *y )
{
	if ( !resolve( (void **)&real_GetMouseState, "SDL_GetMouseState" ) )
	{
		return 0;
	}
	unsigned r = real_GetMouseState( x, y );
	if ( mode() != 2 || !x || !y )
	{
		return r;
	}
	if ( !resolve( (void **)&real_GetMouseFocus, "SDL_GetMouseFocus" ) ||
		!resolve( (void **)&real_GetWindowID, "SDL_GetWindowID" ) )
	{
		return r;
	}
	SDL_Window *f = real_GetMouseFocus();
	unsigned fid = f ? real_GetWindowID( f ) : 0;
	for ( int k = 0; k < MAX_GHOSTS; k++ )
	{
		if ( g_ok[k] && fid == g_wid[k] )
		{
			*x -= g_vx[k];
			*y -= g_vy[k];
			n_remap_state++;
			break;
		}
	}
	return r;
}

__attribute__( ( destructor ) ) static void sdlwinfix_summary( void )
{
	if ( mode() == 0 )
	{
		return;
	}
	WindowEntry *sm = main_entry();
	emitf( "t=%lld summary create=%lu getsize=%lu getposition=%lu faked=%lu moved=%lu main=%u xvec=%lu remap_ev=%lu remap_state=%lu\n",
		now_ms(), n_create, n_getsize, n_getposition, n_faked, n_moved,
		sm ? sm->id : 0, n_xvec, n_remap_ev, n_remap_state );
	if ( log_fd >= 0 )
	{
		close( log_fd );
		log_fd = -1;
	}
	pthread_mutex_lock( &g_dpy_mutex );
	x_drop_locked();
	pthread_mutex_unlock( &g_dpy_mutex );
}
