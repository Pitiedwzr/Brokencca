#import "BCVideoDecoder.h"
#import <VideoToolbox/VideoToolbox.h>
#import <CoreMedia/CoreMedia.h>

@interface BCDecodeContext : NSObject
@property(nonatomic) uint64_t frame, generation;
@property(nonatomic, strong) BCVideoGeometry *geometry;
@end
@implementation BCDecodeContext
@end

@interface BCVideoDecoder () {
    VTDecompressionSessionRef _session;
    CMVideoFormatDescriptionRef _format;
}
@property(nonatomic, strong) dispatch_queue_t queue;
@property(nonatomic) uint64_t generation;
@property(nonatomic, strong) BCVideoGeometry *geometry;
@end

static void BCDecoded(void *refcon, void *frameRefcon, OSStatus status, VTDecodeInfoFlags flags,
                      CVImageBufferRef image, CMTime presentation, CMTime duration) {
    BCVideoDecoder *decoder = (__bridge BCVideoDecoder *)refcon;
    BCDecodeContext *context = (__bridge_transfer BCDecodeContext *)frameRefcon;
    if (status || !image) { if (decoder.failed) decoder.failed(@"VideoToolbox decode failed",context.generation); return; }
    CGSize coded = context.geometry.codedSize;
    if (CVPixelBufferGetPixelFormatType(image) != kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange ||
        CVPixelBufferGetPlaneCount(image) != 2 || CVPixelBufferGetWidth(image) != (size_t)coded.width || CVPixelBufferGetHeight(image) != (size_t)coded.height) {
        if (decoder.failed) decoder.failed(@"Decoder output format/dimensions changed",context.generation); return;
    }
    if (decoder.decoded) decoder.decoded(image,context.frame,context.generation,context.geometry);
}

@implementation BCVideoDecoder
- (instancetype)init { if ((self=[super init])) _queue=dispatch_queue_create("org.brokencca.video.decode",DISPATCH_QUEUE_SERIAL); return self; }
- (void)stopInternal {
    self.generation=0;
    if (_session) { VTDecompressionSessionWaitForAsynchronousFrames(_session); VTDecompressionSessionInvalidate(_session); CFRelease(_session); _session=NULL; }
    if (_format) { CFRelease(_format); _format=NULL; } self.geometry=nil;
}
- (void)configure:(NSDictionary *)config generation:(uint64_t)generation completion:(void (^)(BOOL,BOOL,NSString *))completion {
    dispatch_async(self.queue, ^{
        [self stopInternal];
        NSData *sps=[[NSData alloc] initWithBase64EncodedString:config[@"sps"] options:0];
        NSData *pps=[[NSData alloc] initWithBase64EncodedString:config[@"pps"] options:0];
        if (!VTIsHardwareDecodeSupported(kCMVideoCodecType_H264)) { completion(NO,NO,@"Hardware H.264 decode unavailable"); return; }
        const uint8_t *parameters[]={sps.bytes,pps.bytes}; size_t sizes[]={sps.length,pps.length};
        OSStatus status=CMVideoFormatDescriptionCreateFromH264ParameterSets(kCFAllocatorDefault,2,parameters,sizes,4,&self->_format);
        if (status) { completion(NO,NO,@"Invalid H.264 parameter sets"); return; }
        CMVideoDimensions dimensions=CMVideoFormatDescriptionGetDimensions(self->_format);
        if (dimensions.width!=[config[@"codedWidth"] intValue] || dimensions.height!=[config[@"codedHeight"] intValue]) {
            [self stopInternal]; completion(NO,NO,@"SPS dimensions disagree with configuration"); return;
        }
        NSMutableDictionary *specification=[NSMutableDictionary dictionary];
        if (@available(iOS 17.0,*)) specification[(__bridge NSString *)kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder]=@YES;
        NSDictionary *attributes=@{(__bridge NSString *)kCVPixelBufferPixelFormatTypeKey:@(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange),
            (__bridge NSString *)kCVPixelBufferMetalCompatibilityKey:@YES,(__bridge NSString *)kCVPixelBufferIOSurfacePropertiesKey:@{}};
        VTDecompressionOutputCallbackRecord callbacks={BCDecoded,(__bridge void *)self};
        status=VTDecompressionSessionCreate(kCFAllocatorDefault,self->_format,(__bridge CFDictionaryRef)specification,
            (__bridge CFDictionaryRef)attributes,&callbacks,&self->_session);
        if (status) { [self stopInternal]; completion(NO,NO,@"Could not create hardware decoder"); return; }
        status=VTSessionSetProperty(self->_session,kVTDecompressionPropertyKey_RealTime,kCFBooleanTrue);
        if (status) { [self stopInternal]; completion(NO,NO,@"Decoder real-time mode unavailable"); return; }
        BOOL verified=NO;
        if (@available(iOS 17.0,*)) {
            CFTypeRef value=NULL; status=VTSessionCopyProperty(self->_session,kVTDecompressionPropertyKey_UsingHardwareAcceleratedVideoDecoder,kCFAllocatorDefault,&value);
            verified=!status && value && CFEqual(value,kCFBooleanTrue); if (value) CFRelease(value);
            if (!verified) { [self stopInternal]; completion(NO,NO,@"Decoder hardware verification failed"); return; }
        } else NSLog(@"BCCA_VIDEO Hardware H.264 support confirmed; session verification requires iOS 17+");
        self.generation=generation; self.geometry=[[BCVideoGeometry alloc] initWithConfiguration:config];
        completion(YES,verified,nil);
    });
}
- (void)decode:(NSData *)accessUnit frame:(uint64_t)frame capturedUs:(uint64_t)captured generation:(uint64_t)generation {
    dispatch_async(self.queue, ^{
        if (!self->_session || self.generation!=generation) return;
        CMBlockBufferRef block=NULL; CMSampleBufferRef sample=NULL;
        OSStatus status=CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault,NULL,accessUnit.length,kCFAllocatorDefault,NULL,0,accessUnit.length,0,&block);
        if (!status) status=CMBlockBufferReplaceDataBytes(accessUnit.bytes,block,0,accessUnit.length);
        CMSampleTimingInfo timing={CMTimeMake(1,60),CMTimeMake((int64_t)captured,1000000),kCMTimeInvalid}; size_t size=accessUnit.length;
        if (!status) status=CMSampleBufferCreateReady(kCFAllocatorDefault,block,self->_format,1,1,&timing,1,&size,&sample);
        if (status) { if (block) CFRelease(block); if (sample) CFRelease(sample); if (self.failed) self.failed(@"Could not create decode sample",generation); return; }
        BCDecodeContext *context=[BCDecodeContext new]; context.frame=frame; context.generation=generation; context.geometry=self.geometry;
        void *retained=(__bridge_retained void *)context;
        VTDecodeInfoFlags flags=0;
        status=VTDecompressionSessionDecodeFrame(self->_session,sample,kVTDecodeFrame_EnableAsynchronousDecompression,retained,&flags);
        if (status) { CFBridgingRelease(retained); if (self.failed) self.failed(@"Hardware decode submission failed",generation); }
        CFRelease(sample); CFRelease(block);
    });
}
- (void)stop { dispatch_async(self.queue, ^{ [self stopInternal]; }); }
- (void)dealloc { if (_session) { VTDecompressionSessionInvalidate(_session); CFRelease(_session); } if (_format) CFRelease(_format); }
@end
