#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "BCVideoWire.h"

// Reject unknown, escaped, duplicate or missing field names before NSJSONSerialization.
// Only the protocol's flat objects and arrays are allowed; value semantics are checked separately.
static inline bool BCJSONSpace(uint8_t c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }
static inline bool BCVideoJSONFields(const uint8_t *data, size_t length, uint8_t type) {
    const char *hello[] = {"sessionToken","videoVersion"};
    const char *ack[] = {"maxWidth","maxHeight","maxPixels","maxFps","maxAuBytes","profiles","maxLevel"};
    const char *config[] = {"codec","profile","level","codedWidth","codedHeight","fpsNum","fpsDen","bitrateBps","nalLengthBytes","color","rotation","sourceWidth","sourceHeight","crop","contentRect","circle","sps","pps"};
    const char *ready[] = {"hardwareVerified"};
    const char *feedback[] = {"receivedId","decodedId","presentedId","presentedAtUs","pendingDecode","replacedDecoded","thermal","displayMilliHz"};
    const char *request[] = {"reason"}; const char *ping[] = {"t1Us"}; const char *pong[] = {"t1Us","t2Us","t3Us"};
    const char *status[] = {"state","reason"}; const char *error[] = {"code","detail"};
    const char **fields = NULL; size_t count = 0;
    switch (type) {
        case BCVideoHello: fields=hello; count=2; break; case BCVideoHelloAck: fields=ack; count=7; break;
        case BCVideoConfig: fields=config; count=18; break; case BCVideoReady: fields=ready; count=1; break;
        case BCVideoFeedback: fields=feedback; count=8; break; case BCVideoRequestIDR: fields=request; count=1; break;
        case BCVideoClockPing: fields=ping; count=1; break; case BCVideoClockPong: fields=pong; count=3; break;
        case BCVideoStatus: fields=status; count=2; break; case BCVideoError: fields=error; count=2; break;
        default: return false;
    }
    if (!length || length > BCVideoMaxJson) return false;
    size_t p=0; while (p<length && BCJSONSpace(data[p])) p++;
    if (p==length || data[p++]!='{') return false;
    uint32_t seen=0;
    while (p<length) {
        while (p<length && BCJSONSpace(data[p])) p++;
        if (p<length && data[p]=='}') { p++; break; }
        if (p==length || data[p++]!='"') return false;
        size_t start=p; while (p<length && data[p]!='"') { if (data[p]=='\\' || data[p]<32) return false; p++; }
        if (p==length) return false;
        int index=-1;
        for (size_t i=0;i<count;i++) if (strlen(fields[i])==p-start && !memcmp(data+start,fields[i],p-start)) index=(int)i;
        if (index<0 || (seen & (1u<<index))) return false;
        seen |= 1u<<index; p++;
        while (p<length && BCJSONSpace(data[p])) p++;
        if (p==length || data[p++]!=':') return false;
        int arrays=0; bool string=false, escape=false; size_t valueStart=p;
        for (;p<length;p++) {
            uint8_t c=data[p];
            if (string) { if (escape) escape=false; else if (c=='\\') escape=true; else if (c=='"') string=false; continue; }
            if (c=='"') { string=true; continue; }
            if (c=='{') return false;
            if (c=='[') { if (++arrays>2) return false; continue; }
            if (c==']') { if (--arrays<0) return false; continue; }
            if (!arrays && (c==',' || c=='}')) break;
        }
        if (string || arrays || p==length || p==valueStart) return false;
        if (data[p]=='}') { p++; break; }
        p++; while (p<length && BCJSONSpace(data[p])) p++;
        if (p==length || data[p]=='}') return false;
    }
    while (p<length && BCJSONSpace(data[p])) p++;
    return p==length && seen==((1u<<count)-1);
}
