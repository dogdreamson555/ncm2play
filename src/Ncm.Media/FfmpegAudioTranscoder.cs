using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Ncm.Media;

internal sealed unsafe class FfmpegAudioTranscoder : IAudioTranscoder
{
    private static readonly object NativeLoadLock = new();
    private static readonly AVIOInterruptCB_callback InterruptCallback = InterruptNativeCall;
    private static bool _nativeLibrariesLoaded;

    public Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
        Task.Run(() => Probe(inputPath, cancellationToken), cancellationToken);

    public Task TranscodeAsync(
        string inputPath,
        string outputPath,
        AudioTranscodePlan plan,
        CancellationToken cancellationToken) =>
        Task.Run(() => Transcode(inputPath, outputPath, plan, cancellationToken), cancellationToken);

    private static AudioSourceInfo Probe(string inputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureNativeLibrariesLoaded();
            using var input = new AudioInput(inputPath, cancellationToken);
            return input.GetSourceInfo();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (Exception exception) when (IsNativeLoadFailure(exception))
        {
            throw NativeUnavailable(exception);
        }
        catch (Exception exception)
        {
            throw new MediaExportException("FFmpeg 无法读取音频文件。", exception);
        }
    }

    private static void Transcode(
        string inputPath,
        string outputPath,
        AudioTranscodePlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullInputPath = Path.GetFullPath(inputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new MediaExportException("转码输出路径不能与输入路径相同。");
        }

        if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
        {
            throw new MediaExportException("转码目标已存在，已取消写入。");
        }

        try
        {
            EnsureNativeLibrariesLoaded();
            using var session = new TranscodeSession(fullInputPath, fullOutputPath, plan, cancellationToken);
            session.Run();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (Exception exception) when (IsNativeLoadFailure(exception))
        {
            throw NativeUnavailable(exception);
        }
        catch (Exception exception)
        {
            throw new MediaExportException("FFmpeg 音频转码失败。", exception);
        }
    }

    internal static void EnsureNativeLibrariesLoaded()
    {
        lock (NativeLoadLock)
        {
            if (_nativeLibrariesLoaded)
            {
                return;
            }

            var appDirectory = AppContext.BaseDirectory;
            var runtimeDirectory = Path.Combine(appDirectory, "runtimes", "win-x64", "native");
            ffmpeg.RootPath = File.Exists(Path.Combine(appDirectory, "avcodec-63.dll"))
                ? appDirectory
                : runtimeDirectory;

            try
            {
                RequireMajorVersion("libavcodec", ffmpeg.avcodec_version(), 63);
                RequireMajorVersion("libavformat", ffmpeg.avformat_version(), 63);
                RequireMajorVersion("libavutil", ffmpeg.avutil_version(), 61);
                RequireMajorVersion("libswresample", ffmpeg.swresample_version(), 7);
                RequireMajorVersion("libswscale", ffmpeg.swscale_version(), 10);
                _nativeLibrariesLoaded = true;
            }
            catch (Exception exception) when (IsNativeLoadFailure(exception))
            {
                throw NativeUnavailable(exception);
            }
        }
    }

    private static void RequireMajorVersion(string library, uint version, int expectedMajor)
    {
        var major = (int)(version >> 16);
        if (major != expectedMajor)
        {
            throw new MediaExportException(
                $"FFmpeg 组件版本不匹配：{library} 主版本为 {major}，需要 {expectedMajor}。请重新发布应用目录。" );
        }
    }

    private static MediaExportException NativeUnavailable(Exception exception) =>
        new("无法加载应用附带的 FFmpeg 原生组件。请确认发布目录包含 win-x64 FFmpeg DLL。", exception);

    private static bool IsNativeLoadFailure(Exception exception) => exception switch
    {
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException => true,
        TypeInitializationException { InnerException: { } innerException } =>
            IsNativeLoadFailure(innerException),
        _ => false
    };

    private static void Check(int result, string operation, CancellationToken cancellationToken)
    {
        if (result >= 0)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || result == ffmpeg.AVERROR_EXIT)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new MediaExportException($"FFmpeg {operation}失败：{GetErrorMessage(result)}");
    }

    private static string GetErrorMessage(int error)
    {
        byte* buffer = stackalloc byte[1024];
        var result = ffmpeg.av_strerror(error, buffer, 1024);
        return result < 0
            ? $"错误码 {error}"
            : Marshal.PtrToStringUTF8((nint)buffer) ?? $"错误码 {error}";
    }

    private static int InterruptNativeCall(void* opaque)
    {
        var handle = GCHandle.FromIntPtr((nint)opaque);
        return handle.Target is CancellationToken token && token.IsCancellationRequested ? 1 : 0;
    }

    private sealed class AudioInput : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private GCHandle _interruptHandle;
        private AVFormatContext* _formatContext;
        private AVCodecContext* _decoderContext;
        private AVStream* _stream;

        internal AudioInput(string inputPath, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            if (!File.Exists(inputPath))
            {
                throw new MediaExportException("找不到需要读取的音频文件。");
            }

            _interruptHandle = GCHandle.Alloc(cancellationToken);
            try
            {
                _formatContext = ffmpeg.avformat_alloc_context();
                if (_formatContext is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配输入上下文。");
                }

                _formatContext->interrupt_callback = new AVIOInterruptCB
                {
                    callback = InterruptCallback,
                    opaque = (void*)GCHandle.ToIntPtr(_interruptHandle)
                };

                var formatContext = _formatContext;
                var openResult = ffmpeg.avformat_open_input(&formatContext, inputPath, null, null);
                _formatContext = formatContext;
                Check(openResult, "打开输入文件", cancellationToken);
                Check(ffmpeg.avformat_find_stream_info(_formatContext, null), "读取音频流信息", cancellationToken);

                var streamIndex = ffmpeg.av_find_best_stream(
                    _formatContext,
                    AVMediaType.AVMEDIA_TYPE_AUDIO,
                    -1,
                    -1,
                    null,
                    0);
                Check(streamIndex, "查找音频流", cancellationToken);
                _stream = _formatContext->streams[streamIndex];

                var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
                if (codec is null)
                {
                    throw new MediaExportException("FFmpeg 没有可用的音频解码器。");
                }

                _decoderContext = ffmpeg.avcodec_alloc_context3(codec);
                if (_decoderContext is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配音频解码上下文。");
                }

                Check(
                    ffmpeg.avcodec_parameters_to_context(_decoderContext, _stream->codecpar),
                    "读取音频解码参数",
                    cancellationToken);
                Check(ffmpeg.avcodec_open2(_decoderContext, codec, null), "打开音频解码器", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal AVFormatContext* FormatContext => _formatContext;

        internal AVCodecContext* DecoderContext => _decoderContext;

        internal AVStream* Stream => _stream;

        internal AudioSourceInfo GetSourceInfo()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var sampleRate = _decoderContext->sample_rate > 0
                ? _decoderContext->sample_rate
                : _stream->codecpar->sample_rate;
            var channels = _decoderContext->ch_layout.nb_channels > 0
                ? _decoderContext->ch_layout.nb_channels
                : _stream->codecpar->ch_layout.nb_channels;
            var codecId = _stream->codecpar->codec_id;
            int? bitsPerSample = null;
            if (codecId == AVCodecID.AV_CODEC_ID_FLAC)
            {
                var sourceBits = _stream->codecpar->bits_per_raw_sample;
                if (sourceBits <= 0)
                {
                    sourceBits = _decoderContext->bits_per_raw_sample;
                }

                if (sourceBits > 0)
                {
                    bitsPerSample = sourceBits;
                }
            }

            var duration = GetDuration(_formatContext, _stream);
            var codecFormat = codecId switch
            {
                AVCodecID.AV_CODEC_ID_MP3 => Ncm.Core.NcmAudioFormat.Mp3,
                AVCodecID.AV_CODEC_ID_FLAC => Ncm.Core.NcmAudioFormat.Flac,
                _ => (Ncm.Core.NcmAudioFormat?)null
            };
            var bitrateKbps = _stream->codecpar->bit_rate > 0
                ? (int?)Math.Round(_stream->codecpar->bit_rate / 1000.0)
                : null;
            return new AudioSourceInfo(sampleRate, channels, bitsPerSample, duration, codecFormat, bitrateKbps);
        }

        public void Dispose()
        {
            if (_decoderContext is not null)
            {
                var decoderContext = _decoderContext;
                ffmpeg.avcodec_free_context(&decoderContext);
                _decoderContext = null;
            }

            if (_formatContext is not null)
            {
                var formatContext = _formatContext;
                ffmpeg.avformat_close_input(&formatContext);
                _formatContext = null;
            }

            if (_interruptHandle.IsAllocated)
            {
                _interruptHandle.Free();
            }
        }
    }

    private sealed class TranscodeSession : IDisposable
    {
        private readonly AudioInput _input;
        private readonly AudioTranscodePlan _plan;
        private readonly CancellationToken _cancellationToken;
        private GCHandle _outputInterruptHandle;
        private AVFormatContext* _outputContext;
        private AVCodecContext* _encoderContext;
        private AVStream* _outputStream;
        private AVAudioFifo* _fifo;
        private SwrContext* _resampler;
        private AVPacket* _inputPacket;
        private AVPacket* _outputPacket;
        private long _nextOutputPts;

        internal TranscodeSession(
            string inputPath,
            string outputPath,
            AudioTranscodePlan plan,
            CancellationToken cancellationToken)
        {
            _plan = plan;
            _cancellationToken = cancellationToken;
            _input = new AudioInput(inputPath, cancellationToken);
            try
            {
                ConfigureOutput(outputPath);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Run()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Check(ffmpeg.avformat_write_header(_outputContext, null), "写入输出文件头", _cancellationToken);

            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var readResult = ffmpeg.av_read_frame(_input.FormatContext, _inputPacket);
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                Check(readResult, "读取音频包", _cancellationToken);
                try
                {
                    if (_inputPacket->stream_index == _input.Stream->index)
                    {
                        DecodePacket(_inputPacket);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_inputPacket);
                }
            }

            FlushDecoder();
            FlushResampler();
            DrainFifo(flush: true);
            FlushEncoder();
            Check(ffmpeg.av_write_trailer(_outputContext), "写入输出文件结尾", _cancellationToken);
        }

        private void ConfigureOutput(string outputPath)
        {
            if (_plan.SampleRate <= 0 || _plan.Channels <= 0 || _plan.Channels > 8)
            {
                throw new MediaExportException("目标采样率或声道数无效。");
            }

            var outputFormat = _plan.OutputFormat switch
            {
                Ncm.Core.NcmAudioFormat.Mp3 => "mp3",
                Ncm.Core.NcmAudioFormat.Flac => "flac",
                _ => throw new MediaExportException("FFmpeg 只支持输出 MP3 或 FLAC。")
            };

            var encoder = _plan.OutputFormat switch
            {
                Ncm.Core.NcmAudioFormat.Mp3 => FindMp3Encoder(),
                Ncm.Core.NcmAudioFormat.Flac => ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC),
                _ => null
            };
            if (encoder is null)
            {
                throw new MediaExportException(
                    _plan.OutputFormat == Ncm.Core.NcmAudioFormat.Mp3
                        ? "FFmpeg 当前构建没有 libmp3lame 编码器，无法导出 MP3。"
                        : "FFmpeg 当前构建没有 FLAC 编码器，无法导出 FLAC。");
            }

            AVFormatContext* outputContext = null;
            var allocateResult = ffmpeg.avformat_alloc_output_context2(
                &outputContext,
                null,
                outputFormat,
                outputPath);
            _outputContext = outputContext;
            Check(allocateResult, "创建输出容器", _cancellationToken);
            if (_outputContext is null)
            {
                throw new MediaExportException("FFmpeg 无法创建目标音频容器。");
            }

            _encoderContext = ffmpeg.avcodec_alloc_context3(encoder);
            if (_encoderContext is null)
            {
                throw new MediaExportException("FFmpeg 无法分配音频编码上下文。");
            }

            ConfigureEncoder();
            AVDictionary* encoderOptions = null;
            try
            {
                if (_plan.OutputFormat == Ncm.Core.NcmAudioFormat.Mp3)
                {
                    Check(ffmpeg.av_dict_set(&encoderOptions, "abr", "0", 0), "设置 MP3 固定码率", _cancellationToken);
                }

                Check(ffmpeg.avcodec_open2(_encoderContext, encoder, &encoderOptions), "打开音频编码器", _cancellationToken);
            }
            finally
            {
                ffmpeg.av_dict_free(&encoderOptions);
            }

            _outputStream = ffmpeg.avformat_new_stream(_outputContext, encoder);
            if (_outputStream is null)
            {
                throw new MediaExportException("FFmpeg 无法创建输出音频流。");
            }

            _outputStream->time_base = _encoderContext->time_base;
            Check(
                ffmpeg.avcodec_parameters_from_context(_outputStream->codecpar, _encoderContext),
                "写入编码参数",
                _cancellationToken);

            if ((_outputContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                _encoderContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                Check(
                    ffmpeg.avcodec_parameters_from_context(_outputStream->codecpar, _encoderContext),
                    "写入全局编码参数",
                    _cancellationToken);
            }

            SwrContext* resampler = null;
            var resamplerResult = ffmpeg.swr_alloc_set_opts2(
                    &resampler,
                    &_encoderContext->ch_layout,
                    _encoderContext->sample_fmt,
                    _plan.SampleRate,
                    &_input.DecoderContext->ch_layout,
                    _input.DecoderContext->sample_fmt,
                    _input.DecoderContext->sample_rate,
                    0,
                    null);
            _resampler = resampler;
            Check(
                resamplerResult,
                "配置音频重采样器",
                _cancellationToken);
            if (_resampler is null)
            {
                throw new MediaExportException("FFmpeg 无法配置音频重采样器。");
            }

            Check(ffmpeg.swr_init(_resampler), "初始化音频重采样器", _cancellationToken);

            _fifo = ffmpeg.av_audio_fifo_alloc(
                _encoderContext->sample_fmt,
                _plan.Channels,
                Math.Max(_encoderContext->frame_size, 1));
            if (_fifo is null)
            {
                throw new MediaExportException("FFmpeg 无法分配音频缓冲区。");
            }

            _inputPacket = ffmpeg.av_packet_alloc();
            _outputPacket = ffmpeg.av_packet_alloc();
            if (_inputPacket is null || _outputPacket is null)
            {
                throw new MediaExportException("FFmpeg 无法分配音频数据包。");
            }

            OpenOutputIo(outputPath);
        }

        private void OpenOutputIo(string outputPath)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _outputInterruptHandle = GCHandle.Alloc(_cancellationToken);
            var interruptCallback = new AVIOInterruptCB
            {
                callback = InterruptCallback,
                opaque = (void*)GCHandle.ToIntPtr(_outputInterruptHandle)
            };
            _outputContext->interrupt_callback = interruptCallback;

            if ((_outputContext->oformat->flags & ffmpeg.AVFMT_NOFILE) != 0)
            {
                return;
            }

            Check(
                ffmpeg.avio_open2(
                    &_outputContext->pb,
                    outputPath,
                    ffmpeg.AVIO_FLAG_WRITE,
                    &interruptCallback,
                    null),
                "打开输出文件",
                _cancellationToken);
        }

        private void ConfigureEncoder()
        {
            var context = _encoderContext;
            context->sample_rate = _plan.SampleRate;
            context->time_base = new AVRational { num = 1, den = _plan.SampleRate };
            context->bit_rate = _plan.OutputFormat switch
            {
                Ncm.Core.NcmAudioFormat.Mp3 when _plan.Mp3BitrateKbps is { } bitrateKbps =>
                    checked((long)bitrateKbps * 1000),
                Ncm.Core.NcmAudioFormat.Mp3 => throw new MediaExportException("MP3 码率未设置。"),
                _ => 0
            };

            if (_plan.OutputFormat == Ncm.Core.NcmAudioFormat.Mp3)
            {
                if (_plan.Channels is not (1 or 2) || _plan.Mp3BitrateKbps is not (128 or 192 or 256 or 320))
                {
                    throw new MediaExportException("MP3 转码参数不受支持。");
                }

                context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            }
            else
            {
                var bitsPerSample = _plan.BitsPerSample;
                context->sample_fmt = bitsPerSample switch
                {
                    16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
                    24 => AVSampleFormat.AV_SAMPLE_FMT_S32,
                    _ => throw new MediaExportException("FLAC 仅支持 16 或 24 bit 输出。")
                };
                context->bits_per_raw_sample = bitsPerSample.Value;
                context->compression_level = _plan.FlacCompressionLevel is >= 0 and <= 8
                    ? _plan.FlacCompressionLevel.Value
                    : throw new MediaExportException("FLAC 压缩级别必须在 0 到 8 之间。");
            }

            var inputLayout = _input.DecoderContext->ch_layout;
            ffmpeg.av_channel_layout_uninit(&context->ch_layout);
            if (_plan.OutputFormat == Ncm.Core.NcmAudioFormat.Flac &&
                inputLayout.nb_channels == _plan.Channels &&
                inputLayout.nb_channels > 0 &&
                inputLayout.order != AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                Check(
                    ffmpeg.av_channel_layout_copy(&context->ch_layout, &inputLayout),
                    "复制声道布局",
                    _cancellationToken);
            }
            else
            {
                ffmpeg.av_channel_layout_default(&context->ch_layout, _plan.Channels);
            }

            if ((_outputContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }
        }

        private static AVCodec* FindMp3Encoder()
        {
            var encoder = ffmpeg.avcodec_find_encoder_by_name("libmp3lame");
            if (encoder is not null)
            {
                return encoder;
            }

            return null;
        }

        private void DecodePacket(AVPacket* packet)
        {
            var sendResult = ffmpeg.avcodec_send_packet(_input.DecoderContext, packet);
            if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                DrainDecoder();
                sendResult = ffmpeg.avcodec_send_packet(_input.DecoderContext, packet);
            }

            Check(sendResult, "提交待解码音频", _cancellationToken);
            DrainDecoder();
        }

        private void DrainDecoder()
        {
            var frame = ffmpeg.av_frame_alloc();
            if (frame is null)
            {
                throw new MediaExportException("FFmpeg 无法分配解码帧。");
            }

            try
            {
                while (true)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var result = ffmpeg.avcodec_receive_frame(_input.DecoderContext, frame);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    {
                        return;
                    }

                    Check(result, "解码音频帧", _cancellationToken);
                    ConvertFrame(frame);
                    ffmpeg.av_frame_unref(frame);
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&frame);
            }
        }

        private void ConvertFrame(AVFrame* inputFrame)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var outputSampleCapacity = ffmpeg.swr_get_out_samples(_resampler, inputFrame->nb_samples);
            Check(outputSampleCapacity, "计算重采样缓冲区大小", _cancellationToken);
            if (outputSampleCapacity == 0)
            {
                return;
            }

            var convertedFrame = ffmpeg.av_frame_alloc();
            if (convertedFrame is null)
            {
                throw new MediaExportException("FFmpeg 无法分配重采样帧。");
            }

            try
            {
                convertedFrame->nb_samples = outputSampleCapacity;
                convertedFrame->format = (int)_encoderContext->sample_fmt;
                convertedFrame->sample_rate = _plan.SampleRate;
                Check(
                    ffmpeg.av_channel_layout_copy(&convertedFrame->ch_layout, &_encoderContext->ch_layout),
                    "设置重采样帧声道布局",
                    _cancellationToken);
                Check(ffmpeg.av_frame_get_buffer(convertedFrame, 0), "分配重采样帧数据", _cancellationToken);

                var convertedSamples = ffmpeg.swr_convert(
                    _resampler,
                    convertedFrame->extended_data,
                    outputSampleCapacity,
                    inputFrame->extended_data,
                    inputFrame->nb_samples);
                Check(convertedSamples, "重采样音频帧", _cancellationToken);
                if (convertedSamples == 0)
                {
                    return;
                }

                convertedFrame->nb_samples = convertedSamples;
                Check(
                    ffmpeg.av_audio_fifo_realloc(_fifo, ffmpeg.av_audio_fifo_size(_fifo) + convertedSamples),
                    "扩展音频缓冲区",
                    _cancellationToken);
                var written = ffmpeg.av_audio_fifo_write(_fifo, (void**)convertedFrame->extended_data, convertedSamples);
                if (written != convertedSamples)
                {
                    throw new MediaExportException("FFmpeg 未能完整缓冲重采样音频帧。");
                }

                DrainFifo(flush: false);
            }
            finally
            {
                ffmpeg.av_frame_free(&convertedFrame);
            }
        }

        private void FlushDecoder()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var sendResult = ffmpeg.avcodec_send_packet(_input.DecoderContext, null);
            if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                DrainDecoder();
                sendResult = ffmpeg.avcodec_send_packet(_input.DecoderContext, null);
            }

            if (sendResult != ffmpeg.AVERROR_EOF)
            {
                Check(sendResult, "刷新解码器", _cancellationToken);
            }

            DrainDecoder();
        }

        private void FlushResampler()
        {
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var outputSampleCapacity = ffmpeg.swr_get_out_samples(_resampler, 0);
                Check(outputSampleCapacity, "计算重采样尾帧大小", _cancellationToken);
                if (outputSampleCapacity == 0)
                {
                    return;
                }

                var frame = ffmpeg.av_frame_alloc();
                if (frame is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配重采样尾帧。");
                }

                try
                {
                    frame->nb_samples = outputSampleCapacity;
                    frame->format = (int)_encoderContext->sample_fmt;
                    frame->sample_rate = _plan.SampleRate;
                    Check(
                        ffmpeg.av_channel_layout_copy(&frame->ch_layout, &_encoderContext->ch_layout),
                        "设置重采样尾帧声道布局",
                        _cancellationToken);
                    Check(ffmpeg.av_frame_get_buffer(frame, 0), "分配重采样尾帧数据", _cancellationToken);
                    var convertedSamples = ffmpeg.swr_convert(
                        _resampler,
                        frame->extended_data,
                        outputSampleCapacity,
                        null,
                        0);
                    Check(convertedSamples, "刷新音频重采样器", _cancellationToken);
                    if (convertedSamples == 0)
                    {
                        return;
                    }

                    frame->nb_samples = convertedSamples;
                    Check(
                        ffmpeg.av_audio_fifo_realloc(_fifo, ffmpeg.av_audio_fifo_size(_fifo) + convertedSamples),
                        "扩展音频缓冲区",
                        _cancellationToken);
                    var written = ffmpeg.av_audio_fifo_write(_fifo, (void**)frame->extended_data, convertedSamples);
                    if (written != convertedSamples)
                    {
                        throw new MediaExportException("FFmpeg 未能完整缓冲重采样尾帧。");
                    }

                    DrainFifo(flush: false);
                }
                finally
                {
                    ffmpeg.av_frame_free(&frame);
                }
            }
        }

        private void DrainFifo(bool flush)
        {
            var frameSize = _encoderContext->frame_size > 0
                ? _encoderContext->frame_size
                : 4096;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var queuedSamples = ffmpeg.av_audio_fifo_size(_fifo);
                if (queuedSamples < frameSize && (!flush || queuedSamples == 0))
                {
                    return;
                }

                var samplesToEncode = Math.Min(queuedSamples, frameSize);
                var supportsSmallLastFrame =
                    (_encoderContext->codec->capabilities & ffmpeg.AV_CODEC_CAP_SMALL_LAST_FRAME) != 0;
                if (flush && samplesToEncode < frameSize && !supportsSmallLastFrame)
                {
                    samplesToEncode = frameSize;
                }

                var frame = ffmpeg.av_frame_alloc();
                if (frame is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配编码帧。");
                }

                try
                {
                    frame->nb_samples = samplesToEncode;
                    frame->format = (int)_encoderContext->sample_fmt;
                    frame->sample_rate = _plan.SampleRate;
                    frame->pts = _nextOutputPts;
                    Check(
                        ffmpeg.av_channel_layout_copy(&frame->ch_layout, &_encoderContext->ch_layout),
                        "设置编码帧声道布局",
                        _cancellationToken);
                    Check(ffmpeg.av_frame_get_buffer(frame, 0), "分配编码帧数据", _cancellationToken);

                    var samplesToRead = Math.Min(queuedSamples, samplesToEncode);
                    var read = ffmpeg.av_audio_fifo_read(_fifo, (void**)frame->extended_data, samplesToRead);
                    if (read != samplesToRead)
                    {
                        throw new MediaExportException("FFmpeg 未能完整读取待编码音频帧。");
                    }

                    if (samplesToRead < samplesToEncode)
                    {
                        Check(
                            ffmpeg.av_samples_set_silence(
                                frame->extended_data,
                                samplesToRead,
                                samplesToEncode - samplesToRead,
                                _plan.Channels,
                                _encoderContext->sample_fmt),
                            "填充最后一帧音频",
                            _cancellationToken);
                    }

                    _nextOutputPts += samplesToEncode;
                    EncodeFrame(frame);
                }
                finally
                {
                    ffmpeg.av_frame_free(&frame);
                }
            }
        }

        private void EncodeFrame(AVFrame* frame)
        {
            var sendResult = ffmpeg.avcodec_send_frame(_encoderContext, frame);
            if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                DrainEncoder();
                sendResult = ffmpeg.avcodec_send_frame(_encoderContext, frame);
            }

            Check(sendResult, "提交待编码音频", _cancellationToken);
            DrainEncoder();
        }

        private void FlushEncoder()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var sendResult = ffmpeg.avcodec_send_frame(_encoderContext, null);
            if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                DrainEncoder();
                sendResult = ffmpeg.avcodec_send_frame(_encoderContext, null);
            }

            if (sendResult != ffmpeg.AVERROR_EOF)
            {
                Check(sendResult, "刷新编码器", _cancellationToken);
            }

            DrainEncoder();
        }

        private void DrainEncoder()
        {
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var result = ffmpeg.avcodec_receive_packet(_encoderContext, _outputPacket);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    return;
                }

                Check(result, "接收编码音频包", _cancellationToken);
                try
                {
                    ffmpeg.av_packet_rescale_ts(
                        _outputPacket,
                        _encoderContext->time_base,
                        _outputStream->time_base);
                    _outputPacket->stream_index = _outputStream->index;
                    Check(
                        ffmpeg.av_interleaved_write_frame(_outputContext, _outputPacket),
                        "写入编码音频包",
                        _cancellationToken);
                }
                finally
                {
                    ffmpeg.av_packet_unref(_outputPacket);
                }
            }
        }

        public void Dispose()
        {
            if (_inputPacket is not null)
            {
                var packet = _inputPacket;
                ffmpeg.av_packet_free(&packet);
                _inputPacket = null;
            }

            if (_outputPacket is not null)
            {
                var packet = _outputPacket;
                ffmpeg.av_packet_free(&packet);
                _outputPacket = null;
            }

            if (_fifo is not null)
            {
                ffmpeg.av_audio_fifo_free(_fifo);
            }

            if (_resampler is not null)
            {
                var resampler = _resampler;
                ffmpeg.swr_free(&resampler);
                _resampler = null;
            }

            if (_encoderContext is not null)
            {
                var encoderContext = _encoderContext;
                ffmpeg.avcodec_free_context(&encoderContext);
                _encoderContext = null;
            }

            if (_outputContext is not null)
            {
                if (_outputContext->pb is not null &&
                    _outputContext->oformat is not null &&
                    (_outputContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    ffmpeg.avio_closep(&_outputContext->pb);
                }

                ffmpeg.avformat_free_context(_outputContext);
                _outputContext = null;
            }

            if (_outputInterruptHandle.IsAllocated)
            {
                _outputInterruptHandle.Free();
            }

            _input.Dispose();
        }
    }

    private static TimeSpan? GetDuration(AVFormatContext* formatContext, AVStream* stream)
    {
        if (stream->duration > 0 && stream->time_base.den > 0)
        {
            var seconds = stream->duration * (double)stream->time_base.num / stream->time_base.den;
            if (double.IsFinite(seconds) && seconds > 0 && seconds <= TimeSpan.MaxValue.TotalSeconds)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        if (formatContext->duration > 0)
        {
            var seconds = formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
            if (double.IsFinite(seconds) && seconds <= TimeSpan.MaxValue.TotalSeconds)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }
}
