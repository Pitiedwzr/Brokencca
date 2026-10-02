#pragma once
#include <stdbool.h>

// Access under the view's lock. One coalesced main-thread request, one drawable
// awaiting presentation, and at most two GPU submissions across generations.
typedef struct { bool queued, paused; unsigned gpu, presenting; } BCVideoSchedule;
typedef struct { bool gpuFinished, presentationFinished; } BCVideoRenderTicket;

static inline bool BCVideoRequestRender(BCVideoSchedule *s, bool ready) {
    if (!ready || s->paused || s->queued || s->gpu>=2 || s->presenting>=1) return false;
    s->queued=true; return true;
}
static inline bool BCVideoBeginRender(BCVideoSchedule *s, bool ready) {
    s->queued=false;
    if (!ready || s->paused || s->gpu>=2 || s->presenting>=1) return false;
    s->gpu++; s->presenting++; return true;
}
static inline void BCVideoPresentationFinished(BCVideoSchedule *s, BCVideoRenderTicket *ticket) {
    if (!ticket->presentationFinished) { ticket->presentationFinished=true; s->presenting--; }
}
static inline void BCVideoGPUFinished(BCVideoSchedule *s, BCVideoRenderTicket *ticket, bool failed) {
    if (!ticket->gpuFinished) { ticket->gpuFinished=true; s->gpu--; }
    if (failed) BCVideoPresentationFinished(s,ticket);
}
