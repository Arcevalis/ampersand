// libxconfinecapture - TEMPORARY scene-view edge-snap for XWayland.
//
// The editor warps the cursor edge-to-edge mid-drag (LockCursorToCanvas),
// but on XWayland plain warps never land mid-drag: the Wayland implicit
// button-grab owns the cursor, and explicit X grabs from any in-process
// connection fail AlreadyGrabbed (proven over Stages 1-4: ~200x failed
// grabs, 198/198 forwarded warps dropped). The one mechanism that works is
// XTEST fake motion, which travels the device path where position tracks
// even under a grab (Stage 5: snap confirmed working with capture ON,
// absent with capture OFF on a clean tree).
//
// This shim is a stopgap until Facepunch fixes cursor capture natively.
// It does the minimum: on a buttons-held drag warp it re-issues the warp
// destination as XTestFakeMotionEvent (libXtst via dlopen, no link dep)
// and verifies by sync query. No grabs, no confinement, no probes, no
// discovery walks - every grab-era path has been deleted. The original
// warp is always forwarded, never swallowed.
//
// Scope: fires only on drag warps (buttons held, verified by a synchronous
// query inside the warp hook), at most once per 500ms. Releasing the mouse
// button stops all injection immediately; XTEST installs no grab, so there
// is nothing that can wedge input. First injection may trigger the
// compositor's remote-input consent prompt; that grant is what makes the
// snap (and only the snap, mid-drag) work.
//
// Hook at the xcb level, not just Xlib: Qt 5.15 xcb issues warps via
// xcb_warp_pointer directly. xcb types are redeclared here (exact xcbproto
// layouts) so the build needs no libxcb headers; Xlib types come from X11/Xlib.h.
// All real calls go through dlsym(RTLD_NEXT) pointers, never the interposed
// names, so there is no reentrancy.
//
// SBOX_XCONFCAPTURE unset/0/off/false/empty = dormant passthrough, no logging.
// Anything else ("1", ...) = armed, logging unconditional while armed.
// Log goes to XCONFCAPTURE_LOG or logs/xconfinecapture.log.
//
// Wiring: apps/_common.sh appends this to LD_PRELOAD (after HarfBuzz) when
// SBOX_XCONFCAPTURE is set; override the path with SBOX_XCONFCAPTURE_SO.
// Only --env allowlisted variables cross into the Steam runtime container,
// so use host-side runs for trials.
//
// Build: gcc -D_GNU_SOURCE -shared -fPIC -O1 -o libxconfinecapture.so xconfinecapture.c -ldl
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

// ---- minimal xcb declarations (exact xcbproto layouts, no libxcb headers) ----
typedef struct xcb_connection_s xcb_connection_t;
typedef uint32_t xcb_window_t;
typedef struct { unsigned int sequence; } xcb_void_cookie_t;
typedef struct { unsigned int sequence; } xcb_query_pointer_cookie_t;
typedef struct { int dummy; } xcb_generic_error_t;
typedef struct
{
	uint8_t response_type;
	uint8_t same_screen;
	uint16_t sequence;
	uint32_t length;
	uint32_t root;
	uint32_t child;
	int16_t root_x;
	int16_t root_y;
	int16_t win_x;
	int16_t win_y;
	uint16_t mask;
	uint16_t pad;
} xcb_query_pointer_reply_t;

// Root lookup for translating warp coords (xcb geometry reply layout).
typedef struct { unsigned int sequence; } xcb_get_geometry_cookie_t;
typedef struct
{
	uint8_t response_type;
	uint8_t depth;
	uint16_t sequence;
	uint32_t length;
	uint32_t root;
	int16_t x;
	int16_t y;
	uint16_t width;
	uint16_t height;
	uint16_t border_width;
	uint8_t pad[10];
} xcb_get_geometry_reply_t;

// X button mask bits inside the pointer state mask (xcb + Xlib agree).
#define BTN_MASK 0x1F00

// Anti-spam: at most one XTEST injection per drag every 500ms (the proven
// Stage-5 rate - every-frame injection is unnecessary).
#define XTEST_COOLDOWN_MS 500

static xcb_void_cookie_t (*real_xcb_warp_pointer)( xcb_connection_t *, xcb_window_t, xcb_window_t,
	int16_t, int16_t, uint16_t, uint16_t, int16_t, int16_t ) = 0;
static xcb_void_cookie_t (*real_xcb_warp_pointer_checked)( xcb_connection_t *, xcb_window_t, xcb_window_t,
	int16_t, int16_t, uint16_t, uint16_t, int16_t, int16_t ) = 0;
static xcb_query_pointer_cookie_t (*real_xcb_query_pointer)( xcb_connection_t *, xcb_window_t ) = 0;
static xcb_query_pointer_reply_t *(*real_xcb_query_pointer_reply)( xcb_connection_t *,
	xcb_query_pointer_cookie_t, xcb_generic_error_t ** ) = 0;
static xcb_get_geometry_cookie_t (*real_xcb_get_geometry)( xcb_connection_t *, xcb_window_t ) = 0;
static xcb_get_geometry_reply_t *(*real_xcb_get_geometry_reply)( xcb_connection_t *,
	xcb_get_geometry_cookie_t, xcb_generic_error_t ** ) = 0;

static int (*real_XWarpPointer)( Display *, Window, Window, int, int,
	unsigned int, unsigned int, int, int ) = 0;
static Bool (*real_XQueryPointer)( Display *, Window, Window *, Window *,
	int *, int *, int *, int *, unsigned int * ) = 0;
static int (*real_XTranslateCoordinates)( Display *, Window, Window, int, int,
	int *, int *, Window * ) = 0;
static int (*real_XFlush)( Display * ) = 0;
static Display *(*real_XOpenDisplay)( const char * ) = 0;
// XTEST fake motion: libXtst via dlopen, no link dep.
static int (*real_XTestFakeMotionEvent)( Display *, int, int, int, unsigned long ) = 0;
static void *xtst_handle = 0;
static int xtest_unavail_logged = 0;

// First Xlib Display opened in this process (XOpenDisplay hook). XTEST
// needs a Display; XIDs and root coordinates are server-global so any
// in-process Display addresses the same sprite.
static Display *xtest_disp = 0;
static int have_disp = 0;
static long long last_xtest_ms = 0;

static unsigned long n_warp = 0;
static unsigned long n_warp_checked = 0;
static unsigned long n_xwarp = 0;
static unsigned long n_xtest = 0;
static unsigned long n_xtest_moved = 0;

static int log_fd = -1;
static int log_failed = 0;
static int attached = 0;

static int mode_cached = -1;
static int mode( void )
{
	if ( mode_cached >= 0 )
	{
		return mode_cached;
	}
	const char *v = getenv( "SBOX_XCONFCAPTURE" );
	int m = 0;
	if ( v && v[0] && strcmp( v, "0" ) != 0 && strcmp( v, "off" ) != 0 && strcmp( v, "false" ) != 0 )
	{
		m = 1;
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
	const char *path = getenv( "XCONFCAPTURE_LOG" );
	if ( !path || !path[0] )
	{
		path = "logs/xconfinecapture.log";
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

static void attach_once( void )
{
	if ( attached || mode() == 0 )
	{
		return;
	}
	attached = 1;
	emitf( "t=%lld attach pid=%d stage=6(xtest-only)\n", now_ms(), (int)getpid() );
}

static void *resolve( void **slot, const char *name )
{
	if ( !*slot )
	{
		*slot = dlsym( RTLD_NEXT, name );
	}
	return *slot;
}

// Synchronous pointer query on an xcb connection. Returns 1 with position and
// mask when the roundtrip works; 0 when it cannot.
static int sync_query_xcb( xcb_connection_t *c, xcb_window_t w,
	int *root_x, int *root_y, unsigned int *mask )
{
	xcb_generic_error_t *e = 0;
	xcb_query_pointer_reply_t *r;

	resolve( (void **)&real_xcb_query_pointer, "xcb_query_pointer" );
	resolve( (void **)&real_xcb_query_pointer_reply, "xcb_query_pointer_reply" );
	if ( !real_xcb_query_pointer || !real_xcb_query_pointer_reply )
	{
		return 0;
	}
	r = real_xcb_query_pointer_reply( c, real_xcb_query_pointer( c, w ), &e );
	if ( !r )
	{
		return 0;
	}
	*root_x = r->root_x;
	*root_y = r->root_y;
	*mask = r->mask;
	free( r );
	return 1;
}

// Synchronous button check on an xcb connection. Returns 1 with *held set when
// the query roundtrips; 0 when it cannot (caller decides the default).
static int buttons_held_xcb( xcb_connection_t *c, xcb_window_t w, int *held )
{
	int rx = 0, ry = 0;
	unsigned int mask = 0;

	if ( !sync_query_xcb( c, w, &rx, &ry, &mask ) )
	{
		return 0;
	}
	*held = ( mask & BTN_MASK ) != 0;
	return 1;
}

// Root window of w on the warping connection (for coordinate translation).
// Returns 0 when unknown - caller then uses the warp coords as-is.
static xcb_window_t root_of( xcb_connection_t *c, xcb_window_t w )
{
	xcb_generic_error_t *e = 0;
	xcb_get_geometry_reply_t *g;
	xcb_window_t root = 0;

	resolve( (void **)&real_xcb_get_geometry, "xcb_get_geometry" );
	resolve( (void **)&real_xcb_get_geometry_reply, "xcb_get_geometry_reply" );
	if ( !real_xcb_get_geometry || !real_xcb_get_geometry_reply )
	{
		return 0;
	}
	g = real_xcb_get_geometry_reply( c, real_xcb_get_geometry( c, w ), &e );
	if ( !g )
	{
		return 0;
	}
	root = g->root;
	free( g );
	return root;
}

static int xtest_ensure( void )
{
	if ( real_XTestFakeMotionEvent )
	{
		return 1;
	}
	if ( !xtst_handle )
	{
		xtst_handle = dlopen( "libXtst.so.6", RTLD_LAZY );
		if ( !xtst_handle )
		{
			xtst_handle = dlopen( "libXtst.so", RTLD_LAZY );
		}
	}
	if ( xtst_handle )
	{
		real_XTestFakeMotionEvent =
			( int (*)( Display *, int, int, int, unsigned long ) )dlsym( xtst_handle, "XTestFakeMotionEvent" );
	}
	if ( !real_XTestFakeMotionEvent )
	{
		if ( !xtest_unavail_logged )
		{
			xtest_unavail_logged = 1;
			emitf( "t=%lld xtest unavailable (no libXtst)\n", now_ms() );
		}
		return 0;
	}
	return 1;
}

// The whole mechanism: fake device-path motion to (x, y), flushed and
// verified by sync query. Installs no grab - nothing to release, nothing
// that can wedge input.
static void xtest_fire( Display *d, xcb_connection_t *c, xcb_window_t verify_win, int x, int y )
{
	int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
	unsigned int m1 = 0, m2 = 0;
	int moved;

	if ( !xtest_ensure() )
	{
		return;
	}
	resolve( (void **)&real_XFlush, "XFlush" );
	if ( !real_XFlush )
	{
		return;
	}
	if ( !sync_query_xcb( c, verify_win, &x1, &y1, &m1 ) )
	{
		return;
	}
	real_XTestFakeMotionEvent( d, 0, x, y, 0 );
	real_XFlush( d );
	if ( !sync_query_xcb( c, verify_win, &x2, &y2, &m2 ) )
	{
		return;
	}
	moved = ( x2 != x1 || y2 != y1 );
	n_xtest++;
	if ( moved )
	{
		n_xtest_moved++;
	}
	emitf( "t=%lld xtest to=(%d,%d) before=(%d,%d) after=(%d,%d) moved=%d\n",
		now_ms(), x, y, x1, y1, x2, y2, moved );
	(void)m1;
	(void)m2;
}

// Shared warp entry (xcb path): on buttons-held drag warps only, re-issue
// the destination via XTEST. The caller forwards the original warp itself -
// always, never swallowed. Non-drag warps pass through untouched.
static void on_warp_xcb( xcb_connection_t *c, xcb_window_t src_window, xcb_window_t dst_window,
	int dst_x, int dst_y )
{
	int held = 0;
	int known;
	int ax = dst_x, ay = dst_y;
	xcb_window_t root;

	if ( last_xtest_ms != 0 && now_ms() - last_xtest_ms < XTEST_COOLDOWN_MS )
	{
		return;
	}
	if ( dst_window == 0 )
	{
		emitf( "t=%lld warp NO-WIN src=0x%x\n", now_ms(), src_window );
		return;
	}
	known = buttons_held_xcb( c, dst_window, &held );
	if ( known && !held )
	{
		// Not a drag (e.g. a centre warp with buttons up): leave it alone.
		return;
	}
	if ( !have_disp || !xtest_disp )
	{
		return;
	}
	root = root_of( c, dst_window );
	if ( root != 0 && root != dst_window )
	{
		Window child = 0;

		resolve( (void **)&real_XTranslateCoordinates, "XTranslateCoordinates" );
		if ( real_XTranslateCoordinates &&
			!real_XTranslateCoordinates( xtest_disp, dst_window, root, dst_x, dst_y, &ax, &ay, &child ) )
		{
			ax = dst_x;
			ay = dst_y;
		}
	}
	last_xtest_ms = now_ms();
	xtest_fire( xtest_disp, c, root != 0 ? root : dst_window, ax, ay );
	(void)src_window;
}

// ---- interposed functions: xcb level (what Qt actually calls) ----

xcb_void_cookie_t xcb_warp_pointer( xcb_connection_t *c, xcb_window_t src_window, xcb_window_t dst_window,
	int16_t src_x, int16_t src_y, uint16_t src_width, uint16_t src_height, int16_t dst_x, int16_t dst_y )
{
	resolve( (void **)&real_xcb_warp_pointer, "xcb_warp_pointer" );
	n_warp++;
	if ( mode() != 0 )
	{
		attach_once();
		emitf( "t=%lld warp src=0x%x dst=0x%x dst=%d,%d\n",
			now_ms(), src_window, dst_window, dst_x, dst_y );
		on_warp_xcb( c, src_window, dst_window, dst_x, dst_y );
	}
	if ( !real_xcb_warp_pointer )
	{
		xcb_void_cookie_t empty = { 0 };
		return empty;
	}
	return real_xcb_warp_pointer( c, src_window, dst_window,
		src_x, src_y, src_width, src_height, dst_x, dst_y );
}

xcb_void_cookie_t xcb_warp_pointer_checked( xcb_connection_t *c, xcb_window_t src_window, xcb_window_t dst_window,
	int16_t src_x, int16_t src_y, uint16_t src_width, uint16_t src_height, int16_t dst_x, int16_t dst_y )
{
	resolve( (void **)&real_xcb_warp_pointer_checked, "xcb_warp_pointer_checked" );
	n_warp_checked++;
	if ( mode() != 0 )
	{
		attach_once();
		emitf( "t=%lld warp_checked src=0x%x dst=0x%x dst=%d,%d\n",
			now_ms(), src_window, dst_window, dst_x, dst_y );
		on_warp_xcb( c, src_window, dst_window, dst_x, dst_y );
	}
	if ( !real_xcb_warp_pointer_checked )
	{
		xcb_void_cookie_t empty = { 0 };
		return empty;
	}
	return real_xcb_warp_pointer_checked( c, src_window, dst_window,
		src_x, src_y, src_width, src_height, dst_x, dst_y );
}

// ---- interposed functions: Xlib level (covers any Xlib-side callers) ----

int XWarpPointer( Display *display, Window src_w, Window dest_w, int src_x, int src_y,
	unsigned int src_width, unsigned int src_height, int dest_x, int dest_y )
{
	resolve( (void **)&real_XWarpPointer, "XWarpPointer" );
	resolve( (void **)&real_XQueryPointer, "XQueryPointer" );
	resolve( (void **)&real_XTranslateCoordinates, "XTranslateCoordinates" );
	n_xwarp++;
	if ( mode() != 0 )
	{
		attach_once();
		emitf( "t=%lld xwarp src=0x%lx dst=0x%lx dst=%d,%d\n",
			now_ms(), (unsigned long)src_w, (unsigned long)dest_w, dest_x, dest_y );
		if ( dest_w != 0 && real_XQueryPointer &&
			( last_xtest_ms == 0 || now_ms() - last_xtest_ms >= XTEST_COOLDOWN_MS ) )
		{
			Window root_r = 0, child_r = 0;
			int rx = 0, ry = 0, wx = 0, wy = 0;
			unsigned int mask = 0;
			if ( real_XQueryPointer( display, dest_w, &root_r, &child_r,
					&rx, &ry, &wx, &wy, &mask ) &&
				( mask & BTN_MASK ) != 0 )
			{
				int ax = dest_x, ay = dest_y;
				Window child = 0;

				if ( root_r != 0 && root_r != dest_w && real_XTranslateCoordinates &&
					!real_XTranslateCoordinates( display, dest_w, root_r,
						dest_x, dest_y, &ax, &ay, &child ) )
				{
					ax = dest_x;
					ay = dest_y;
				}
				last_xtest_ms = now_ms();
				if ( xtest_ensure() )
				{
					resolve( (void **)&real_XFlush, "XFlush" );
					if ( real_XFlush )
					{
						real_XTestFakeMotionEvent( display, 0, ax, ay, 0 );
						real_XFlush( display );
						n_xtest++;
						emitf( "t=%lld xtest to=(%d,%d) xlib\n", now_ms(), ax, ay );
					}
				}
			}
		}
	}
	if ( !real_XWarpPointer )
	{
		return 0;
	}
	return real_XWarpPointer( display, src_w, dest_w, src_x, src_y,
		src_width, src_height, dest_x, dest_y );
}

// First Display opened in this process - the Display XTEST injects through.
// XIDs and root coordinates are server-global, so any one will do.
Display *XOpenDisplay( const char *name )
{
	Display *d;

	resolve( (void **)&real_XOpenDisplay, "XOpenDisplay" );
	if ( !real_XOpenDisplay )
	{
		return 0;
	}
	d = real_XOpenDisplay( name );
	if ( mode() != 0 && d != 0 )
	{
		attach_once();
		if ( !have_disp )
		{
			xtest_disp = d;
			have_disp = 1;
			emitf( "t=%lld xdisplay open disp=%p name=%s\n",
				now_ms(), (void *)d, name != 0 ? name : "(default)" );
		}
	}
	return d;
}

__attribute__( ( destructor ) ) static void xconfinecapture_summary( void )
{
	if ( mode() == 0 )
	{
		return;
	}
	emitf( "t=%lld summary warp=%lu warp_checked=%lu xwarp=%lu xtest=%lu xtest_moved=%lu have_disp=%d\n",
		now_ms(), n_warp, n_warp_checked, n_xwarp, n_xtest, n_xtest_moved, have_disp );
	if ( log_fd >= 0 )
	{
		close( log_fd );
		log_fd = -1;
	}
}
