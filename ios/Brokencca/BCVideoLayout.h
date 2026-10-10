#pragma once
#include <math.h>
#include <stdbool.h>
#include "BCControllerLayout.h"

typedef enum { BCVideoFullImage = 0, BCVideoFullSizeRing = 1, BCVideoArcade = 2 } BCVideoLayoutMode;

typedef struct {
    double codedWidth, codedHeight, sourceWidth, sourceHeight;
    double crop[4], content[4], circle[3];
} BCVideoLayout;
typedef struct { double x, y, width, height; } BCVideoRect;

// All modes preserve image aspect ratio. Ring modes center the calibrated
// playfield at the full controller radius or its visible inner sensor edge.
static inline BCVideoRect BCVideoCodedRect(BCVideoLayout l, double width, double height, BCVideoLayoutMode mode) {
    if (width<=0 || height<=0 || l.codedWidth<=0 || l.codedHeight<=0) return (BCVideoRect){0,0,0,0};
    double scale=fmin(width/l.codedWidth,height/l.codedHeight);
    double centerX=l.codedWidth/2,centerY=l.codedHeight/2;
    if (mode!=BCVideoFullImage) {
        double rx=l.content[2]/(l.crop[2]*l.sourceWidth),ry=l.content[3]/(l.crop[3]*l.sourceHeight);
        double radius=l.circle[2]*fmin(l.sourceWidth,l.sourceHeight)*fmin(rx,ry);
        if (!isfinite(radius) || radius<=0) return (BCVideoRect){0,0,0,0};
        scale=fmin(width,height)/(2*radius);
        if (mode==BCVideoArcade) scale*=BCSensorVisibleInnerRadiusRatio;
        centerX=l.content[0]+(l.circle[0]-l.crop[0])/l.crop[2]*l.content[2];
        centerY=l.content[1]+(l.circle[1]-l.crop[1])/l.crop[3]*l.content[3];
    }
    return (BCVideoRect){width/2-centerX*scale,height/2-centerY*scale,l.codedWidth*scale,l.codedHeight*scale};
}

static inline BCVideoRect BCVideoContentRect(BCVideoLayout l, BCVideoRect coded) {
    double scale=coded.width/l.codedWidth;
    return (BCVideoRect){coded.x+l.content[0]*scale,coded.y+l.content[1]*scale,l.content[2]*scale,l.content[3]*scale};
}

static inline bool BCVideoMapPoint(BCVideoLayout l, double width, double height, BCVideoLayoutMode mode,
                                  double x, double y, double *vx, double *vy) {
    if (!isfinite(x) || !isfinite(y) || x<0 || y<0 || x>=width || y>=height) return false;
    if (mode==BCVideoArcade) {
        // Sensor positions are independent of the smaller game image, including
        // sensors outside its content rectangle. Preserve the full controller.
        double radius=fmin(width,height)/2;
        *vx=(x-width/2)/radius+1; *vy=(y-height/2)/radius+1;
        return true;
    }
    BCVideoRect content=BCVideoContentRect(l,BCVideoCodedRect(l,width,height,mode));
    if (content.width<=0 || content.height<=0 || x<content.x || y<content.y ||
        x>=content.x+content.width || y>=content.y+content.height) return false;
    double sourceX=(l.crop[0]+(x-content.x)/content.width*l.crop[2])*l.sourceWidth;
    double sourceY=(l.crop[1]+(y-content.y)/content.height*l.crop[3])*l.sourceHeight;
    double r=l.circle[2]*fmin(l.sourceWidth,l.sourceHeight);
    *vx=(sourceX-l.circle[0]*l.sourceWidth)/r+1;
    *vy=(sourceY-l.circle[1]*l.sourceHeight)/r+1;
    return true;
}

static inline void BCVideoCircleInView(BCVideoLayout l, double width, double height, BCVideoLayoutMode mode,
                                      double *cx, double *cy, double *radius) {
    BCVideoRect content=BCVideoContentRect(l,BCVideoCodedRect(l,width,height,mode));
    *radius=l.circle[2]*fmin(l.sourceWidth,l.sourceHeight)*fmin(content.width/(l.crop[2]*l.sourceWidth),content.height/(l.crop[3]*l.sourceHeight));
    *cx=content.x+(l.circle[0]-l.crop[0])/l.crop[2]*content.width;
    *cy=content.y+(l.circle[1]-l.crop[1])/l.crop[3]*content.height;
}

static inline void BCVideoControllerCircleInView(BCVideoLayout l, double width, double height, BCVideoLayoutMode mode,
                                                double *cx, double *cy, double *radius) {
    if (mode==BCVideoArcade) { *cx=width/2; *cy=height/2; *radius=fmin(width,height)/2; return; }
    BCVideoCircleInView(l,width,height,mode,cx,cy,radius);
}
