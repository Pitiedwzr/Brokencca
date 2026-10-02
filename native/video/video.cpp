// Native Media Foundation/D3D11 bridge. All exported calls belong to one C# video worker.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <d3d11.h>
#include <d3d10.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <evr.h>
#include <codecapi.h>
#include <strmif.h>
#include <atomic>
#include <memory>
#include <vector>
#include <cstdio>
#include <cstring>
#include <stdexcept>
static const GUID CodecApiId = {0x901db4c7,0x31ce,0x41a2,{0x85,0xdc,0x8f,0xa0,0xbf,0x41,0xb8,0xda}};

template<class T> struct Ptr {
    T* p = nullptr;
    Ptr() = default;
    ~Ptr() { reset(); }
    Ptr(const Ptr&) = delete;
    Ptr& operator=(const Ptr&) = delete;
    T* operator->() const { return p; }
    T** put() { reset(); return &p; }
    void reset() { if (p) { p->Release(); p = nullptr; } }
};
static void check(HRESULT hr) { if (FAILED(hr)) throw hr; }
struct Slot {
    Ptr<ID3D11Texture2D> texture;
    Ptr<ID3D11Query> fence;
    std::atomic<bool> busy{false};
};
class ReleaseCallback final : public IMFAsyncCallback {
    std::atomic<ULONG> refs{1};
    std::shared_ptr<Slot> slot;
public:
    explicit ReleaseCallback(std::shared_ptr<Slot> value) : slot(std::move(value)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == IID_IUnknown || id == IID_IMFAsyncCallback) { *result = this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetParameters(DWORD*, DWORD*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Invoke(IMFAsyncResult*) override { slot->busy.store(false); return S_OK; }
};

struct Encoder {
    Ptr<ID3D11Device> device;
    Ptr<ID3D11DeviceContext> context;
    Ptr<ID3D11VideoDevice> video;
    Ptr<ID3D11VideoContext> videoContext;
    Ptr<ID3D11VideoProcessorEnumerator> enumerator;
    Ptr<ID3D11VideoProcessor> processor;
    Ptr<IMFDXGIDeviceManager> manager;
    Ptr<IMFTransform> transform;
    Ptr<IMFMediaEventGenerator> events;
    Ptr<ICodecAPI> codec;
    Ptr<IMFActivate> activation;
    std::vector<std::shared_ptr<Slot>> slots;
    std::shared_ptr<Slot> pending;
    LONGLONG pendingTime = 0;
    UINT width, height;
    DWORD inputId = 0, outputId = 0;
    unsigned credits = 0, outstanding = 0;
    UINT64 sampleIndex = 0;
    bool mfStarted = false, comStarted = false;
    char name[256] = {};
    const char* stage = "create";
    ~Encoder() {
        if (transform.p) {
            transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            Ptr<IMFShutdown> shutdown;
            if (SUCCEEDED(transform->QueryInterface(IID_IMFShutdown, (void**)shutdown.put()))) shutdown->Shutdown();
        }
        events.reset(); codec.reset(); transform.reset();
        if (activation.p) activation->ShutdownObject();
        activation.reset(); manager.reset(); processor.reset(); enumerator.reset();
        videoContext.reset(); video.reset(); context.reset(); device.reset();
        pending.reset(); slots.clear();
        if (mfStarted) MFShutdown();
        if (comStarted) CoUninitialize();
    }
    void setting(const GUID& key, VARTYPE type, ULONG value, bool required) {
        VARIANT v; VariantInit(&v); v.vt = type;
        if (type == VT_BOOL) v.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE; else v.ulVal = value;
        HRESULT hr = codec->SetValue(&key, &v);
        if (required) check(hr);
        else if (FAILED(hr)) std::fprintf(stderr, "VIDEO encoder optional control rejected: 0x%08lx\n", (unsigned long)hr);
    }
    void media(IMFMediaType* type, const GUID& subtype, UINT bitrate) {
        check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check(type->SetGUID(MF_MT_SUBTYPE, subtype));
        check(type->SetUINT64(MF_MT_FRAME_SIZE, ((UINT64)width << 32) | height));
        check(type->SetUINT64(MF_MT_FRAME_RATE, ((UINT64)60 << 32) | 1));
        check(type->SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, ((UINT64)1 << 32) | 1));
        check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        check(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        check(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
        check(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
        check(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
        if (subtype == MFVideoFormat_H264) {
            check(type->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
            check(type->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main));
            check(type->SetUINT32(MF_MT_MPEG2_LEVEL, 42));
        }
    }
    void initialize(ID3D11Device* supplied, UINT w, UINT h, UINT bitrate) {
        width = w; height = h;
        if (!supplied || w < 2 || h < 2 || w > 1920 || h > 1920 || (w & 1) || (h & 1) ||
            (UINT64)w*h > 2073600 || bitrate < 4000000 || bitrate > 20000000) throw E_INVALIDARG;
        stage = "COM/MF startup";
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (SUCCEEDED(hr)) comStarted = true; else check(hr);
        check(MFStartup(MF_VERSION, MFSTARTUP_FULL)); mfStarted = true;
        supplied->AddRef(); device.p = supplied; device->GetImmediateContext(context.put());
        Ptr<ID3D10Multithread> threading;
        check(context->QueryInterface(IID_ID3D10Multithread, (void**)threading.put()));
        threading->SetMultithreadProtected(TRUE);
        stage = "hardware H.264 enumeration";
        MFT_REGISTER_TYPE_INFO in{MFMediaType_Video, MFVideoFormat_NV12}, out{MFMediaType_Video, MFVideoFormat_H264};
        IMFActivate** choices = nullptr; UINT32 count = 0;
        check(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            &in, &out, &choices, &count));
        HRESULT last = MF_E_TOPO_CODEC_NOT_FOUND;
        for (UINT32 i = 0; i < count; ++i) {
            Ptr<IMFTransform> candidate;
            HRESULT activated = choices[i]->ActivateObject(IID_IMFTransform, (void**)candidate.put());
            Ptr<IMFAttributes> attributes;
            UINT32 aware = 0, async = 0;
            if (SUCCEEDED(activated)) activated = candidate->GetAttributes(attributes.put());
            if (SUCCEEDED(activated)) {
                attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware);
                attributes->GetUINT32(MF_TRANSFORM_ASYNC, &async);
                if (!aware || !async) activated = MF_E_UNSUPPORTED_D3D_TYPE;
            }
            if (SUCCEEDED(activated)) activated = attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
            if (SUCCEEDED(activated)) {
                UINT reset = 0;
                activated = MFCreateDXGIDeviceManager(&reset, manager.put());
                if (SUCCEEDED(activated)) activated = manager->ResetDevice(device.p, reset);
                if (SUCCEEDED(activated)) activated = candidate->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)manager.p);
            }
            if (SUCCEEDED(activated)) {
                transform.p = candidate.p; candidate.p = nullptr;
                choices[i]->AddRef(); activation.p = choices[i];
                WCHAR* friendly = nullptr; UINT32 chars = 0;
                if (SUCCEEDED(choices[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &chars))) {
                    WideCharToMultiByte(CP_UTF8, 0, friendly, -1, name, sizeof(name), nullptr, nullptr); CoTaskMemFree(friendly);
                }
                last = S_OK; break;
            }
            last = activated; choices[i]->ShutdownObject();
        }
        for (UINT32 i = 0; i < count; ++i) choices[i]->Release();
        CoTaskMemFree(choices); check(last);
        stage = "low-latency encoder controls";
        check(transform->QueryInterface(CodecApiId, (void**)codec.put()));
        setting(CODECAPI_AVLowLatencyMode, VT_BOOL, 1, true);
        setting(CODECAPI_AVEncMPVDefaultBPictureCount, VT_UI4, 0, true);
        setting(CODECAPI_AVEncMPVGOPSize, VT_UI4, 120, false);
        setting(CODECAPI_AVEncCommonRateControlMode, VT_UI4, eAVEncCommonRateControlMode_CBR, false);
        setting(CODECAPI_AVEncCommonMeanBitRate, VT_UI4, bitrate, false);
        // H.264 MF buffer size is expressed in bytes; request approximately 100 ms.
        setting(CODECAPI_AVEncCommonBufferSize, VT_UI4, bitrate / 80, false);
        VARIANT bufferSize; VariantInit(&bufferSize);
        if (SUCCEEDED(codec->GetValue(&CODECAPI_AVEncCommonBufferSize, &bufferSize)) && bufferSize.vt == VT_UI4)
            std::fprintf(stderr, "VIDEO encoder CBR buffer: %lu bytes (requested %u)\n", (unsigned long)bufferSize.ulVal, bitrate / 80);
        VariantClear(&bufferSize);
        stage = "encoder media types";
        DWORD inputs = 0, outputs = 0;
        check(transform->GetStreamCount(&inputs, &outputs));
        if (inputs != 1 || outputs != 1) throw E_NOTIMPL;
        hr = transform->GetStreamIDs(1, &inputId, 1, &outputId); if (hr != E_NOTIMPL) check(hr);
        Ptr<IMFMediaType> outputType, inputType;
        check(MFCreateMediaType(outputType.put())); media(outputType.p, MFVideoFormat_H264, bitrate);
        check(transform->SetOutputType(outputId, outputType.p, 0));
        check(MFCreateMediaType(inputType.put())); media(inputType.p, MFVideoFormat_NV12, bitrate);
        check(transform->SetInputType(inputId, inputType.p, 0));
        check(transform->QueryInterface(IID_IMFMediaEventGenerator, (void**)events.put()));
        stage = "D3D11 video processor";
        check(device->QueryInterface(IID_ID3D11VideoDevice, (void**)video.put()));
        check(context->QueryInterface(IID_ID3D11VideoContext, (void**)videoContext.put()));
        // The enumerator is recreated on input-size changes; the output pool is fixed for this generation.
        for (unsigned i = 0; i < 4; ++i) {
            auto slot = std::make_shared<Slot>();
            D3D11_TEXTURE2D_DESC desc{}; desc.Width = w; desc.Height = h; desc.MipLevels = desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_NV12; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            check(device->CreateTexture2D(&desc, nullptr, slot->texture.put()));
            D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0}; check(device->CreateQuery(&query, slot->fence.put()));
            slots.push_back(slot);
        }
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
    }
    UINT inputWidth = 0, inputHeight = 0;
    void configureProcessor(UINT w, UINT h) {
        if (inputWidth == w && inputHeight == h) return;
        processor.reset(); enumerator.reset();
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
        desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        desc.InputFrameRate = desc.OutputFrameRate = {60, 1};
        desc.InputWidth = w; desc.InputHeight = h; desc.OutputWidth = width; desc.OutputHeight = height;
        desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check(video->CreateVideoProcessorEnumerator(&desc, enumerator.put()));
        UINT flags = 0;
        check(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_B8G8R8A8_UNORM, &flags));
        if (!(flags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT)) throw MF_E_UNSUPPORTED_D3D_TYPE;
        check(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &flags));
        if (!(flags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT)) throw MF_E_UNSUPPORTED_D3D_TYPE;
        check(video->CreateVideoProcessor(enumerator.p, 0, processor.put()));
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColor{}, outputColor{};
        inputColor.RGB_Range = 0; inputColor.YCbCr_Matrix = 1; inputColor.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
        outputColor.YCbCr_Matrix = 1; outputColor.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        videoContext->VideoProcessorSetStreamColorSpace(processor.p, 0, &inputColor);
        videoContext->VideoProcessorSetOutputColorSpace(processor.p, &outputColor);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.p, 0, FALSE);
        inputWidth = w; inputHeight = h;
    }
    HRESULT submit(ID3D11Texture2D* texture, int x, int y, int w, int h, LONGLONG pts,
                   int left, int top, int targetWidth, int targetHeight) {
        if (!texture || pending || !credits || outstanding >= 3) return S_FALSE;
        std::shared_ptr<Slot> free;
        for (auto& slot : slots) if (!slot->busy.load()) { free = slot; break; }
        if (!free) return S_FALSE;
        D3D11_TEXTURE2D_DESC source{}; texture->GetDesc(&source);
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || (UINT64)x+w > source.Width || (UINT64)y+h > source.Height ||
            left < 0 || top < 0 || targetWidth <= 0 || targetHeight <= 0 || left+targetWidth > (int)width || top+targetHeight > (int)height) throw E_INVALIDARG;
        stage = "GPU crop/scale/NV12"; configureProcessor(source.Width, source.Height);
        Ptr<ID3D11VideoProcessorInputView> input; Ptr<ID3D11VideoProcessorOutputView> output;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{}; in.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{}; out.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        check(video->CreateVideoProcessorInputView(texture, enumerator.p, &in, input.put()));
        check(video->CreateVideoProcessorOutputView(free->texture.p, enumerator.p, &out, output.put()));
        RECT src{x, y, x+w, y+h}, dest{left, top, left+targetWidth, top+targetHeight}, entire{0,0,(LONG)width,(LONG)height};
        D3D11_VIDEO_COLOR black{}; black.YCbCr.Y = 16.0f/255; black.YCbCr.Cb = black.YCbCr.Cr = 128.0f/255;
        videoContext->VideoProcessorSetOutputBackgroundColor(processor.p, TRUE, &black);
        videoContext->VideoProcessorSetOutputTargetRect(processor.p, TRUE, &entire);
        videoContext->VideoProcessorSetStreamSourceRect(processor.p, 0, TRUE, &src);
        videoContext->VideoProcessorSetStreamDestRect(processor.p, 0, TRUE, &dest);
        videoContext->VideoProcessorSetStreamFrameFormat(processor.p, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = input.p;
        check(videoContext->VideoProcessorBlt(processor.p, output.p, 0, 1, &stream));
        free->busy.store(true); context->End(free->fence.p); context->Flush();
        pending = free; pendingTime = pts; return S_OK;
    }
    void pumpInput() {
        if (!pending || !credits) return;
        HRESULT ready = context->GetData(pending->fence.p, nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (ready == S_FALSE) return;
        check(ready); stage = "encoder input";
        Ptr<IMFSample> sample;
        // EVR creates an IMFTrackedSample; attach a D3D11 DXGI buffer explicitly.
        check(MFCreateVideoSampleFromSurface(nullptr, sample.put()));
        Ptr<IMFMediaBuffer> buffer;
        check(MFCreateDXGISurfaceBuffer(IID_ID3D11Texture2D, pending->texture.p, 0, FALSE, buffer.put()));
        check(sample->AddBuffer(buffer.p));
        check(sample->SetSampleTime(pendingTime));
        check(sample->SetSampleDuration((sampleIndex+1)*10000000/60 - sampleIndex*10000000/60));
        ++sampleIndex;
        Ptr<IMFTrackedSample> tracked;
        check(sample->QueryInterface(IID_IMFTrackedSample, (void**)tracked.put()));
        auto callback = new ReleaseCallback(pending);
        HRESULT allocated = tracked->SetAllocator(callback, nullptr); callback->Release(); check(allocated);
        check(transform->ProcessInput(inputId, sample.p, 0));
        --credits; ++outstanding; pending.reset();
    }
    HRESULT poll(BYTE* bytes, UINT capacity, UINT* length, LONGLONG* pts) {
        *length = 0; pumpInput();
        for (unsigned i = 0; i < 16; ++i) {
            Ptr<IMFMediaEvent> event;
            HRESULT hr = events->GetEvent(MF_EVENT_FLAG_NO_WAIT, event.put());
            if (hr == MF_E_NO_EVENTS_AVAILABLE) return S_FALSE;
            check(hr); MediaEventType type; check(event->GetType(&type)); HRESULT status; check(event->GetStatus(&status)); check(status);
            if (type == METransformNeedInput) { if (credits < 16) ++credits; pumpInput(); }
            if (type != METransformHaveOutput) continue;
            stage = "encoder output";
            MFT_OUTPUT_STREAM_INFO info{}; check(transform->GetOutputStreamInfo(outputId, &info));
            Ptr<IMFSample> provided;
            if (!(info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                check(MFCreateSample(provided.put())); Ptr<IMFMediaBuffer> memory;
                if (info.cbSize > capacity) throw MF_E_BUFFERTOOSMALL;
                check(MFCreateMemoryBuffer(capacity, memory.put())); check(provided->AddBuffer(memory.p));
            }
            MFT_OUTPUT_DATA_BUFFER output{}; output.dwStreamID = outputId; output.pSample = provided.p;
            DWORD outputStatus = 0;
            hr = transform->ProcessOutput(0, 1, &output, &outputStatus);
            if (output.pEvents) output.pEvents->Release();
            Ptr<IMFSample> supplied;
            if (output.pSample && output.pSample != provided.p) supplied.p = output.pSample;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
                Ptr<IMFMediaType> changed;
                check(transform->GetOutputAvailableType(outputId, 0, changed.put()));
                check(transform->SetOutputType(outputId, changed.p, 0)); continue;
            }
            check(hr); if (!output.pSample) throw E_UNEXPECTED;
            Ptr<IMFMediaBuffer> buffer; check(output.pSample->ConvertToContiguousBuffer(buffer.put()));
            check(output.pSample->GetSampleTime(pts));
            DWORD size = 0; check(buffer->GetCurrentLength(&size)); if (!size || size > capacity) throw MF_E_BUFFERTOOSMALL;
            BYTE* data = nullptr; check(buffer->Lock(&data, nullptr, nullptr)); std::memcpy(bytes, data, size); buffer->Unlock();
            *length = size; if (outstanding) --outstanding; return S_OK;
        }
        return S_FALSE;
    }
};

#define EXPORT extern "C" __declspec(dllexport)
EXPORT HRESULT bc_video_create(ID3D11Device* device, UINT width, UINT height, UINT bitrate, Encoder** result, char* detail, UINT detailSize) {
    if (!result || !detail || !detailSize) return E_POINTER;
    *result = nullptr;
    std::unique_ptr<Encoder> encoder(new Encoder());
    try { encoder->initialize(device,width,height,bitrate); std::snprintf(detail, detailSize, "%s", encoder->name); *result=encoder.release(); return S_OK; }
    catch (HRESULT hr) { std::snprintf(detail,detailSize,"%s: 0x%08lx",encoder->stage,(unsigned long)hr); return hr; }
    catch (...) { std::snprintf(detail,detailSize,"Native encoder exception"); return E_FAIL; }
}
EXPORT HRESULT bc_video_submit(Encoder* encoder, ID3D11Texture2D* texture, int x, int y, int w, int h, LONGLONG pts,
    int left, int top, int targetWidth, int targetHeight) {
    if (!encoder) return E_POINTER;
    try { return encoder->submit(texture,x,y,w,h,pts,left,top,targetWidth,targetHeight); }
    catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
EXPORT HRESULT bc_video_poll(Encoder* encoder, BYTE* bytes, UINT capacity, UINT* length, LONGLONG* pts) {
    if (!encoder || !bytes || !length || !pts) return E_POINTER;
    try { return encoder->poll(bytes,capacity,length,pts); }
    catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
EXPORT HRESULT bc_video_idr(Encoder* encoder) {
    if (!encoder) return E_POINTER;
    try { encoder->setting(CODECAPI_AVEncVideoForceKeyFrame, VT_UI4, 1, true); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
EXPORT HRESULT bc_video_headers(Encoder* encoder, BYTE* bytes, UINT capacity, UINT* length) {
    if (!encoder || !bytes || !length) return E_POINTER;
    try {
        Ptr<IMFMediaType> type; check(encoder->transform->GetOutputCurrentType(encoder->outputId, type.put()));
        HRESULT hr = type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, bytes, capacity, length);
        if (hr == MF_E_ATTRIBUTENOTFOUND) { *length = 0; return S_FALSE; }
        return hr;
    } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
EXPORT void bc_video_destroy(Encoder* encoder) { delete encoder; }
