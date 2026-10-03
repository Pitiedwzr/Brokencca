#pragma once
#include <stdbool.h>
#include <stdint.h>

// Access under the view's lock. One coalesced main-thread request and at most
// two drawables awaiting presentation / GPU submissions across generations.
// Presentation can take two refresh intervals; one pending drawable halves fps.
typedef struct { bool queued, paused; unsigned gpu, presenting; uint64_t lastRefreshUs; } BCVideoSchedule;
typedef struct { bool gpuFinished, presentationFinished; } BCVideoRenderTicket;
typedef struct { uint64_t generation, rendererRevision, layoutRevision; } BCVideoRenderSnapshot;

static inline bool BCVideoRenderSnapshotCurrent(BCVideoRenderSnapshot snapshot,
    uint64_t generation, uint64_t rendererRevision, uint64_t layoutRevision) {
    return snapshot.generation && snapshot.generation==generation &&
        snapshot.rendererRevision==rendererRevision && snapshot.layoutRevision==layoutRevision;
}

static inline bool BCVideoRequestRender(BCVideoSchedule *s, bool ready) {
    if (!ready || s->paused || s->queued || s->gpu>=2 || s->presenting>=2) return false;
    s->queued=true; return true;
}
static inline bool BCVideoBeginRender(BCVideoSchedule *s, bool ready) {
    s->queued=false;
    if (!ready || s->paused || s->gpu>=2 || s->presenting>=2) return false;
    s->gpu++; s->presenting++; return true;
}
// A drawable can be admitted only once for a display refresh. Decode and Metal
// callbacks may mark work pending, but cannot submit a second frame in that cycle.
static inline bool BCVideoBeginRefreshRender(BCVideoSchedule *s, bool ready, uint64_t refreshUs) {
    if (!refreshUs || refreshUs<=s->lastRefreshUs) return false;
    if (!BCVideoBeginRender(s,ready)) return false;
    s->lastRefreshUs=refreshUs; return true;
}
static inline void BCVideoPresentationFinished(BCVideoSchedule *s, BCVideoRenderTicket *ticket) {
    if (!ticket->presentationFinished) { ticket->presentationFinished=true; s->presenting--; }
}
static inline void BCVideoGPUFinished(BCVideoSchedule *s, BCVideoRenderTicket *ticket, bool failed) {
    if (!ticket->gpuFinished) { ticket->gpuFinished=true; s->gpu--; }
    if (failed) BCVideoPresentationFinished(s,ticket);
}
