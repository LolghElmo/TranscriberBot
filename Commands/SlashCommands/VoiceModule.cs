using System.Collections.Concurrent;
using NAudio.Wave;
using AssemblyAI;
using AssemblyAI.Transcripts;
using NetCord.Gateway.Voice;
using NetCord.Services.ApplicationCommands;
using NetCord;
using NetCord.Gateway;
using System.Diagnostics;
using NetCord.Rest;
using TranscriberBot.Data.Models;
using TranscriberBot.Data.Handler;
using System.Threading.Channels;
using Whisper.net.Ggml;
using Whisper.net;

namespace TranscriberBot.Commands.SlashCommands
{
    [SlashCommand("voice", "Voice session commands")]
    public class VoiceModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private static readonly ConcurrentDictionary<ulong, VoiceSession> _voiceSessions = new();
        private const string voiceModuleConfigPath = "voicemodule.json";
        private const int MaxConcurrentTranscriptions = 5;
        private const int MaxConcurrentTts = 10;
        private static readonly TimeSpan SilenceThreshold = TimeSpan.FromMilliseconds(500);
        private static readonly HttpClient _http = new();

        private static readonly string WhisperModelPath = "ggml-base.bin";
        private static WhisperFactory? _whisperFactory;
        private static readonly SemaphoreSlim _whisperFactoryLock = new(1, 1);

        private static void SaveIgnoreConfig() => JsonHandler.SaveJson(voiceModuleConfigPath, Bot.voiceModuleConfig);



        [SubSlashCommand("join", "Join your current voice channel.")]
        public async Task JoinAsync()
        {
            var guild = Context.Guild!;
            var userId = Context.User.Id;
            if (!guild.VoiceStates.TryGetValue(userId, out var voiceState))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You must be in a voice channel to use this command."));
                return;
            }
            if (_voiceSessions.ContainsKey(guild.Id))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Already joined a voice channel in this guild."));
                return;
            }
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Joining voice channel..."));
            var voiceClient = await Context.Client.JoinVoiceChannelAsync(
                guild.Id,
                voiceState.ChannelId.GetValueOrDefault(),
                new() { RedirectInputStreams = true }
            );
            await voiceClient.StartAsync();
            var whisperFactory = await GetWhisperFactoryAsync();
            var session = new VoiceSession(voiceClient, guild, (TextChannel)Context.Channel, Bot.voiceModuleConfig, whisperFactory);
            _voiceSessions[guild.Id] = session;
        }

        [SubSlashCommand("leave", "Leave the voice channel and disable all features.")]
        public async Task LeaveAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryRemove(guildId, out var session))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Not in a voice channel."));
                return;
            }
            session.DisableTranscription();
            session.DisableWhisperTranscription();
            await session.DisableTtsAsync(Context.Client);
            await session.VoiceClient.CloseAsync();
            await Context.Client.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
            session.Dispose();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Left the voice channel and disabled all features."));
        }

        [SubSlashCommand("enable_tts", "Enable TTS in the current session.")]
        public async Task EnableTtsAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Bot is not in a voice channel. Use /voice join first."));
                return;
            }
            if (session.TtsEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("TTS is already enabled."));
                return;
            }
            await session.EnableTtsAsync(Context.Client, (TextChannel)Context.Channel);
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("TTS enabled."));
        }

        [SubSlashCommand("disable_tts", "Disable TTS in the current session.")]
        public async Task DisableTtsAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session) || !session.TtsEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("TTS is not enabled."));
                return;
            }
            await session.DisableTtsAsync(Context.Client);
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("TTS disabled."));
        }

        [SubSlashCommand("enable_transcripts", "Enable live transcription in the current session (AssemblyAI).")]
        public async Task EnableTranscriptsAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Bot is not in a voice channel. Use /voice join first."));
                return;
            }
            if (session.TranscriptionEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription is already enabled."));
                return;
            }
            session.EnableTranscription();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription enabled (AssemblyAI)."));
        }

        [SubSlashCommand("disable_transcripts", "Disable live transcription in the current session (AssemblyAI).")]
        public async Task DisableTranscriptsAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session) || !session.TranscriptionEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription is not enabled."));
                return;
            }
            session.DisableTranscription();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription disabled (AssemblyAI)."));
        }

        [SubSlashCommand("tts_ignore", "Ignore yourself from TTS.")]
        public async Task TtsIgnoreAsync()
        {
            var userId = Context.User.Id;
            if (Bot.voiceModuleConfig.TtsIgnore.Contains(userId))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You are already ignored for TTS."));
                return;
            }
            Bot.voiceModuleConfig.TtsIgnore.Add(userId);
            SaveIgnoreConfig();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You will now be ignored for TTS."));
        }

        [SubSlashCommand("tts_unignore", "Stop ignoring yourself from TTS.")]
        public async Task TtsUnignoreAsync()
        {
            var userId = Context.User.Id;
            if (!Bot.voiceModuleConfig.TtsIgnore.Remove(userId))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You were not ignored for TTS."));
                return;
            }
            SaveIgnoreConfig();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You will now be processed for TTS."));
        }

        [SubSlashCommand("transcriber_ignore", "Ignore yourself from transcription.")]
        public async Task TranscriberIgnoreAsync()
        {
            var userId = Context.User.Id;
            if (Bot.voiceModuleConfig.TranscriberIgnore.Contains(userId))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You are already ignored for transcription."));
                return;
            }
            Bot.voiceModuleConfig.TranscriberIgnore.Add(userId);
            SaveIgnoreConfig();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You will now be ignored for transcription."));
        }

        [SubSlashCommand("transcriber_unignore", "Stop ignoring yourself from transcription.")]
        public async Task TranscriberUnignoreAsync()
        {
            var userId = Context.User.Id;
            if (!Bot.voiceModuleConfig.TranscriberIgnore.Remove(userId))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You were not ignored for transcription."));
                return;
            }
            SaveIgnoreConfig();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You will now be processed for transcription."));
        }

        [SubSlashCommand("trantest_enable", "Enable Whisper.net transcription in the current session.")]
        public async Task TrantestEnableAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session))
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Bot is not in a voice channel. Use /voice join first."));
                return;
            }
            if (session.WhisperTranscriptionEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Whisper.net transcription already enabled."));
                return;
            }
            session.EnableWhisperTranscription();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription enabled (Whisper.net)."));
        }

        [SubSlashCommand("trantest_disable", "Disable Whisper.net transcription in the current session.")]
        public async Task TrantestDisableAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session) || !session.WhisperTranscriptionEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Whisper.net transcription not enabled."));
                return;
            }
            session.DisableWhisperTranscription();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription disabled (Whisper.net)."));
        }

        private static async Task<WhisperFactory> GetWhisperFactoryAsync()
        {
            if (_whisperFactory != null)
                return _whisperFactory;
            await _whisperFactoryLock.WaitAsync();
            try
            {
                if (_whisperFactory != null)
                    return _whisperFactory;
                if (!File.Exists(WhisperModelPath))
                {
                    using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
                    using var fileWriter = File.OpenWrite(WhisperModelPath);
                    await modelStream.CopyToAsync(fileWriter);
                }
                _whisperFactory = WhisperFactory.FromPath(WhisperModelPath);
                return _whisperFactory;
            }
            finally
            {
                _whisperFactoryLock.Release();
            }
        }

        private class VoiceSession : IDisposable
        {
            public VoiceClient VoiceClient { get; }
            public Guild Guild { get; }
            public TextChannel TextChannel { get; }
            public bool TtsEnabled { get; private set; }
            public bool TranscriptionEnabled { get; private set; }
            public bool WhisperTranscriptionEnabled { get; private set; }
            private Func<Message, ValueTask>? _ttsHandler;
            private Func<VoiceReceiveEventArgs, ValueTask>? _transcriptionHandler;
            private Func<VoiceReceiveEventArgs, ValueTask>? _whisperTranscriptionHandler;
            private readonly SemaphoreSlim _ttsSem = new(MaxConcurrentTts);
            internal readonly SemaphoreSlim _transcriptionSem = new(MaxConcurrentTranscriptions);
            internal readonly SemaphoreSlim _whisperTranscribeSem = new(MaxConcurrentTranscriptions);
            private readonly ConcurrentDictionary<ulong, MemoryStream> _buffers = new();
            private readonly ConcurrentDictionary<ulong, MemoryStream> _whisperBuffers = new();
            private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _silenceCts = new();
            private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _whisperSilenceCts = new();
            private OpusDecoder? _decoder;
            private OpusDecoder? _whisperDecoder;
            internal WaveFormat? _waveFormat;
            internal WaveFormat? _whisperWaveFormat;
            private readonly VoiceModuleConfig _moduleConfig;
            private Channel<string>? _ttsQueue;
            private Task? _ttsProcessingTask;
            private readonly WhisperFactory _whisperFactory;

            public VoiceSession(VoiceClient voiceClient, Guild guild, TextChannel channel, VoiceModuleConfig ignoreConfig, WhisperFactory whisperFactory)
            {
                VoiceClient = voiceClient;
                Guild = guild;
                TextChannel = channel;
                _moduleConfig = ignoreConfig;
                _whisperFactory = whisperFactory;
            }

            public async Task EnableTtsAsync(GatewayClient client, TextChannel channel)
            {
                if (TtsEnabled) return;
                TtsEnabled = true;
                _ttsQueue = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                _ttsProcessingTask = Task.Run(ProcessTtsQueueAsync);
                _ttsHandler = async msg =>
                {
                    if (msg.Author.IsBot || msg.Channel.Id != channel.Id || _moduleConfig.TtsIgnore.Contains(msg.Author.Id)) return;
                    var text = msg.GetAsync().Result.Content;
                    if (string.IsNullOrWhiteSpace(text)) return;
                    await _ttsQueue!.Writer.WriteAsync(text);
                };
                client.MessageCreate += _ttsHandler;
                await VoiceClient.EnterSpeakingStateAsync(SpeakingFlags.Microphone);
            }

            public async Task DisableTtsAsync(GatewayClient client)
            {
                if (!TtsEnabled) return;
                if (_ttsHandler != null) client.MessageCreate -= _ttsHandler;
                _ttsQueue?.Writer.Complete();
                if (_ttsProcessingTask != null) await _ttsProcessingTask;
                TtsEnabled = false;
            }

            private async Task ProcessTtsQueueAsync()
            {
                await foreach (var text in _ttsQueue!.Reader.ReadAllAsync())
                {
                    await _ttsSem.WaitAsync();
                    try
                    {
                        await StreamWithGoogleTtsAsync(text);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"TTS error: {ex}");
                    }
                    finally
                    {
                        _ttsSem.Release();
                    }
                }
            }

            public async Task StreamWithGoogleTtsAsync(string text)
            {
                var url = $"https://translate.google.com/translate_tts?tl=en&client=tw-ob&q={Uri.EscapeDataString(text)}";
                using var resp = await _http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                using var ms = new MemoryStream();
                await resp.Content.CopyToAsync(ms);
                ms.Position = 0;
                using var mp3 = new Mp3FileReader(ms);
                var resampled = new MediaFoundationResampler(mp3, new WaveFormat(48000, 16, 2)) { ResamplerQuality = 60 };
                using var pcmStream = resampled;
                using var opus = new OpusEncodeStream(
                    VoiceClient.CreateOutputStream(normalizeSpeed: true),
                    PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);
                var buffer = new byte[pcmStream.WaveFormat.AverageBytesPerSecond];
                int read;
                while ((read = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                    await opus.WriteAsync(buffer, 0, read);
                await opus.FlushAsync();
            }

            public void EnableTranscription()
            {
                if (TranscriptionEnabled) return;
                TranscriptionEnabled = true;
                _decoder = new OpusDecoder(VoiceChannels.Stereo);
                _waveFormat = new WaveFormat(48000, 16, 2);
                _transcriptionHandler = args =>
                {
                    var uid = args.UserId;
                    if (_moduleConfig.TranscriberIgnore.Contains(uid)) return ValueTask.CompletedTask;
                    var buffer = GetBuffer(uid);
                    var pcm = new byte[Opus.SamplesPerChannel * _waveFormat.Channels * sizeof(short)];
                    _decoder.Decode(args.Frame.Span, pcm);
                    buffer.Write(pcm, 0, pcm.Length);
                    StartSilenceTimer(uid);
                    return ValueTask.CompletedTask;
                };
                VoiceClient.VoiceReceive += _transcriptionHandler;
            }

            public void DisableTranscription()
            {
                if (!TranscriptionEnabled) return;
                if (_transcriptionHandler != null) VoiceClient.VoiceReceive -= _transcriptionHandler;
                TranscriptionEnabled = false;
            }

            public MemoryStream GetBuffer(ulong userId) => _buffers.GetOrAdd(userId, _ => new MemoryStream());
            public MemoryStream ResetBuffer(ulong userId)
            {
                var old = _buffers[userId];
                _buffers[userId] = new MemoryStream();
                return old;
            }
            public void StartSilenceTimer(ulong userId)
            {
                if (_silenceCts.TryGetValue(userId, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }
                var cts = new CancellationTokenSource();
                _silenceCts[userId] = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(SilenceThreshold, cts.Token);
                        var ms = ResetBuffer(userId);
                        await ProcessChunkAsync(userId, Guild, this, ms);
                    }
                    catch (OperationCanceledException) { }
                });
            }

            private static async Task ProcessChunkAsync(ulong userId, Guild guild, VoiceSession session, MemoryStream ms)
            {
                await session._transcriptionSem.WaitAsync();
                try
                {
                    var file = Path.Combine(Path.GetTempPath(), $"transcript_{userId}_{Guid.NewGuid()}.wav");
                    using (var writer = new WaveFileWriter(file, session._waveFormat))
                        writer.Write(ms.ToArray(), 0, (int)ms.Length);
                    var client = new AssemblyAIClient(Bot.botConfig?.AssemblyAIToken ?? "no api key");
                    var transcript = await client.Transcripts.TranscribeAsync(
                        new FileInfo(file),
                        new TranscriptOptionalParams { SpeakerLabels = false }
                    );
                    transcript.EnsureStatusCompleted();
                    if (!string.IsNullOrWhiteSpace(transcript.Text))
                    {
                        var user = await guild.GetUserAsync(userId);
                        await session.TextChannel.SendMessageAsync($"**{user.Username}:** {transcript.Text}");
                    }
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error transcribing {userId}: {ex}");
                }
                finally
                {
                    session._transcriptionSem.Release();
                }
            }

            public void EnableWhisperTranscription()
            {
                if (WhisperTranscriptionEnabled) return;
                WhisperTranscriptionEnabled = true;
                _whisperDecoder = new OpusDecoder(VoiceChannels.Stereo);
                _whisperWaveFormat = new WaveFormat(48000, 16, 2);
                _whisperTranscriptionHandler = args =>
                {
                    var userId = args.UserId;
                    var buf = _whisperBuffers.GetOrAdd(userId, _ => new MemoryStream());
                    var pcm = new byte[Opus.SamplesPerChannel * _whisperWaveFormat.Channels * sizeof(short)];
                    _whisperDecoder.Decode(args.Frame.Span, pcm);
                    buf.Write(pcm, 0, pcm.Length);
                    StartWhisperSilenceTimer(userId);
                    return ValueTask.CompletedTask;
                };
                VoiceClient.VoiceReceive += _whisperTranscriptionHandler;
            }

            public void DisableWhisperTranscription()
            {
                if (!WhisperTranscriptionEnabled) return;
                if (_whisperTranscriptionHandler != null) VoiceClient.VoiceReceive -= _whisperTranscriptionHandler;
                WhisperTranscriptionEnabled = false;
            }

            private void StartWhisperSilenceTimer(ulong userId)
            {
                if (_whisperSilenceCts.TryGetValue(userId, out var old))
                {
                    old.Cancel();
                    old.Dispose();
                }
                var cts = new CancellationTokenSource();
                _whisperSilenceCts[userId] = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(SilenceThreshold, cts.Token);
                        var ms = ResetWhisperBuffer(userId);
                        await ProcessWhisperChunkAsync(userId, ms);
                    }
                    catch (OperationCanceledException) { }
                });
            }

            private MemoryStream ResetWhisperBuffer(ulong uid)
            {
                var old = _whisperBuffers[uid];
                _whisperBuffers[uid] = new MemoryStream();
                return old;
            }

            private async Task ProcessWhisperChunkAsync(ulong userId, MemoryStream ms)
            {
                await _whisperTranscribeSem.WaitAsync();
                try
                {
                    var tempFile = Path.Combine(Path.GetTempPath(), $"whisper_{userId}_{Guid.NewGuid()}_48k.wav");
                    using (var writer = new WaveFileWriter(tempFile, _whisperWaveFormat!))
                        writer.Write(ms.ToArray(), 0, (int)ms.Length);
                    var targetFormat = new WaveFormat(16000, 16, 1);
                    var resampledFile = Path.Combine(Path.GetTempPath(), $"whisper_{userId}_{Guid.NewGuid()}_16k.wav");
                    using (var sourceStream = new AudioFileReader(tempFile))
                    using (var resampler = new MediaFoundationResampler(sourceStream, targetFormat) { ResamplerQuality = 60 })
                    using (var outWriter = new WaveFileWriter(resampledFile, resampler.WaveFormat))
                    {
                        byte[] buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
                        int read;
                        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                            outWriter.Write(buffer, 0, read);
                    }
                    File.Delete(tempFile);
                    var segments = new List<string>();
                    using var audioStream = File.OpenRead(resampledFile);
                    var processor = _whisperFactory
                        .CreateBuilder()
                        .WithGreedySamplingStrategy()
                        .ParentBuilder
                        .WithLanguage("english")
                        .Build();
                    await foreach (var result in processor.ProcessAsync(audioStream))
                        segments.Add(result.Text);
                    var transcript = string.Join(" ", segments);
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        var user = await Guild.GetUserAsync(userId);
                        await TextChannel.SendMessageAsync(new MessageProperties
                        {
                            Content = $"**{user.Username}:** {transcript} \n-# This is still being tested.",
                        });
                    }
                    File.Delete(resampledFile);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Whisper.net error: {ex}");
                }
                finally
                {
                    _whisperTranscribeSem.Release();
                }
            }

            public void Dispose()
            {
                foreach (var ms in _buffers.Values) ms.Dispose();
                foreach (var ms in _whisperBuffers.Values) ms.Dispose();
                foreach (var kvp in _silenceCts.Values)
                {
                    kvp.Cancel();
                    kvp.Dispose();
                }
                foreach (var kvp in _whisperSilenceCts.Values)
                {
                    kvp.Cancel();
                    kvp.Dispose();
                }
                _ttsSem.Dispose();
                _transcriptionSem.Dispose();
                _whisperTranscribeSem.Dispose();
                VoiceClient.Dispose();
            }
        }
    }
}


//[SlashCommand("speechtranscript", "Live voice transcription using local SpeechRecognitionEngine")]
//public class SpeechRecognitionModule : ApplicationCommandModule<ApplicationCommandContext>
//{
//    private static readonly ConcurrentDictionary<ulong, SpeechSession> _sessions = new();
//    private const int MaxConcurrentTranscriptions = 5;
//    private static readonly TimeSpan SilenceThreshold = TimeSpan.FromMilliseconds(500);

//    [SubSlashCommand("start", "Begin live local transcription in your current voice channel.")]
//    public async Task StartAsync()
//    {
//        var guild = Context.Guild!;
//        var userId = Context.User.Id;
//        if (!guild.VoiceStates.TryGetValue(userId, out var vs))
//        {
//            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("You must be in a voice channel to start speech transcription."));
//            return;
//        }
//        if (_sessions.ContainsKey(guild.Id))
//        {
//            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Speech transcription is already running in this guild."));
//            return;
//        }

//        await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Starting live local transcription..."));
//        var voiceClient = await Context.Client.JoinVoiceChannelAsync(guild.Id, vs.ChannelId.GetValueOrDefault(), new() { RedirectInputStreams = true });
//        await voiceClient.StartAsync();

//        var session = new SpeechSession(voiceClient, guild, (TextChannel)Context.Channel);
//        _sessions[guild.Id] = session;

//        session.Handler = args =>
//        {
//            var uid = args.UserId;
//            var buffer = session.GetBuffer(uid);
//            var pcm = new byte[Opus.SamplesPerChannel * sizeof(short)];
//            session.Decoder.Decode(args.Frame.Span, pcm);
//            buffer.Write(pcm, 0, pcm.Length);
//            session.StartSilenceTimer(uid);
//            return ValueTask.CompletedTask;
//        };
//        voiceClient.VoiceReceive += session.Handler;
//    }

//    [SubSlashCommand("stop", "Stop live local transcription.")]
//    public async Task StopAsync()
//    {
//        var guildId = Context.Guild!.Id;
//        if (!_sessions.TryRemove(guildId, out var session))
//        {
//            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("No active speech transcription session to stop."));
//            return;
//        }
//        session.VoiceClient.VoiceReceive -= session.Handler;
//        await session.VoiceClient.CloseAsync();
//        session.Dispose();
//        await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Speech transcription stopped."));
//    }

//    private class SpeechSession : IDisposable
//    {
//        private readonly WaveFormat _inputFormat = new WaveFormat(48000, 16, 1);
//        private readonly WaveFormat _recognizerFormat = new WaveFormat(16000, 16, 1);

//        public VoiceClient VoiceClient { get; }
//        public Guild Guild { get; }
//        public TextChannel TextChannel { get; }
//        public OpusDecoder Decoder { get; }
//        public Func<VoiceReceiveEventArgs, ValueTask> Handler { get; set; } = null!;
//        public SemaphoreSlim Semaphore { get; } = new SemaphoreSlim(MaxConcurrentTranscriptions);

//        private readonly ConcurrentDictionary<ulong, MemoryStream> _buffers = new();
//        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _silenceCts = new();

//        public SpeechSession(VoiceClient vc, Guild guild, TextChannel ch)
//        {
//            VoiceClient = vc;
//            Guild = guild;
//            TextChannel = ch;
//            Decoder = new OpusDecoder(VoiceChannels.Mono);
//        }

//        public MemoryStream GetBuffer(ulong id) => _buffers.GetOrAdd(id, _ => new MemoryStream());

//        public MemoryStream ResetBuffer(ulong id)
//        {
//            var old = GetBuffer(id);
//            _buffers[id] = new MemoryStream();
//            return old;
//        }

//        public void StartSilenceTimer(ulong uid)
//        {
//            if (_silenceCts.TryGetValue(uid, out var oldCts))
//            {
//                oldCts.Cancel();
//                oldCts.Dispose();
//            }
//            var cts = new CancellationTokenSource();
//            _silenceCts[uid] = cts;
//            _ = Task.Run(async () =>
//            {
//                try
//                {
//                    await Task.Delay(SilenceThreshold, cts.Token);
//                    var raw = ResetBuffer(uid);
//                    await ProcessAsync(uid, raw);
//                }
//                catch { }
//            });
//        }

//        private async Task ProcessAsync(ulong userId, MemoryStream rawStream)
//        {
//            await Semaphore.WaitAsync();
//            try
//            {
//                rawStream.Position = 0;
//                using var source = new RawSourceWaveStream(rawStream, _inputFormat);
//                using var resampler = new MediaFoundationResampler(source, _recognizerFormat) { ResamplerQuality = 60 };

//                var tempWav = Path.Combine(Path.GetTempPath(), $"speech_{Guild.Id}_{userId}_{Guid.NewGuid()}.wav");
//                using (var writer = new WaveFileWriter(tempWav, _recognizerFormat))
//                {
//                    var buffer = new byte[_recognizerFormat.AverageBytesPerSecond];
//                    int bytesRead;
//                    while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
//                    {
//                        writer.Write(buffer, 0, bytesRead);
//                    }
//                }

//                using var recognizer = new SpeechRecognitionEngine();
//                recognizer.SetInputToWaveFile(tempWav);
//                var result = recognizer.Recognize();
//                var text = result?.Text ?? "(no speech detected)";

//                var user = await Guild.GetUserAsync(userId);
//                await TextChannel.SendMessageAsync($"**{user.Username}:** {text}");

//                File.Delete(tempWav);
//            }
//            catch { }
//            finally
//            {
//                Semaphore.Release();
//            }
//        }

//        public void Dispose()
//        {
//            foreach (var ms in _buffers.Values) ms.Dispose();
//            foreach (var cts in _silenceCts.Values) cts.Cancel();
//            Semaphore.Dispose();
//        }
//    }
//}
