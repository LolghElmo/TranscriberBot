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

namespace TranscriberBot.Commands.SlashCommands
{
    [SlashCommand("voice", "Voice session commands")]
    public class VoiceModule : ApplicationCommandModule<ApplicationCommandContext>
    {

        private static string _assemblyAIToken;
        private static readonly ConcurrentDictionary<ulong, VoiceSession> _voiceSessions = new();
        private const int MaxConcurrentTranscriptions = 5;
        private const int MaxConcurrentTts = 10;
        private static readonly TimeSpan SilenceThreshold = TimeSpan.FromMilliseconds(500);
        private static readonly HttpClient _http = new();


        public VoiceModule()
        {
            _assemblyAIToken = Bot.configFile.BotToken;
        }
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
            var session = new VoiceSession(voiceClient, guild, (TextChannel)Context.Channel);
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
            session.DisableTts(Context.Client);
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
            session.EnableTts(Context.Client, (TextChannel)Context.Channel);
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
            session.DisableTts(Context.Client);
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("TTS disabled."));
        }

        [SubSlashCommand("enable_transcripts", "Enable live transcription in the current session.")]
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
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription enabled."));
        }

        [SubSlashCommand("disable_transcripts", "Disable live transcription in the current session.")]
        public async Task DisableTranscriptsAsync()
        {
            var guildId = Context.Guild!.Id;
            if (!_voiceSessions.TryGetValue(guildId, out var session) || !session.TranscriptionEnabled)
            {
                await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription is not enabled."));
                return;
            }
            session.DisableTranscription();
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message("Transcription disabled."));
        }

        private class VoiceSession : IDisposable
        {
            public VoiceClient VoiceClient { get; }
            public Guild Guild { get; }
            public TextChannel TextChannel { get; }
            public bool TtsEnabled { get; private set; }
            public bool TranscriptionEnabled { get; private set; }
            private Func<Message, ValueTask>? _ttsHandler;
            private Func<VoiceReceiveEventArgs, ValueTask>? _transcriptionHandler;
            public readonly SemaphoreSlim _ttsSem = new(MaxConcurrentTts);
            public readonly SemaphoreSlim _transcriptionSem = new(MaxConcurrentTranscriptions);
            private readonly ConcurrentDictionary<ulong, MemoryStream> _buffers = new();
            private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _silenceCts = new();
            public OpusDecoder? _decoder;
            public WaveFormat? _waveFormat;

            public VoiceSession(VoiceClient voiceClient, Guild guild, TextChannel channel)
            {
                VoiceClient = voiceClient;
                Guild = guild;
                TextChannel = channel;
            }

            public void EnableTts(GatewayClient client, TextChannel channel)
            {
                if (TtsEnabled) return;
                TtsEnabled = true;
                _ttsHandler = async msg =>
                {
                    if (msg.Author.IsBot || msg.Channel.Id != channel.Id) return;
                    await _ttsSem.WaitAsync();
                    try
                    {
                        var text = msg.GetAsync().Result.Content;
                        if (!string.IsNullOrWhiteSpace(text))
                            await StreamWithGoogleTtsAsync(text);
                    }
                    finally { _ttsSem.Release(); }
                };
                client.MessageCreate += _ttsHandler;
                VoiceClient.EnterSpeakingStateAsync(SpeakingFlags.Microphone);
            }

            public void DisableTts(GatewayClient client)
            {
                if (!TtsEnabled) return;
                if (_ttsHandler != null)
                    client.MessageCreate -= _ttsHandler;
                TtsEnabled = false;
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
                if (_transcriptionHandler != null)
                    VoiceClient.VoiceReceive -= _transcriptionHandler;
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
                    existing.Cancel(); existing.Dispose();
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
                    PcmFormat.Short,
                    VoiceChannels.Stereo,
                    OpusApplication.Audio);
                var buffer = new byte[pcmStream.WaveFormat.AverageBytesPerSecond];
                int read;
                while ((read = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
                    await opus.WriteAsync(buffer, 0, read);
                await opus.FlushAsync();
            }
            public void Dispose()
            {
                foreach (var ms in _buffers.Values) ms.Dispose();
                foreach (var kvp in _silenceCts.Values) { kvp.Cancel(); kvp.Dispose(); }
                _ttsSem.Dispose();
                _transcriptionSem.Dispose();
                VoiceClient.Dispose();
            }
        }

        private static async Task ProcessChunkAsync(ulong userId, Guild guild, VoiceSession session, MemoryStream ms)
        {
            await session._transcriptionSem.WaitAsync();
            try
            {
                var file = Path.Combine(Path.GetTempPath(), $"transcript_{userId}_{Guid.NewGuid()}.wav");
                using (var writer = new WaveFileWriter(file, session._waveFormat))
                    writer.Write(ms.ToArray(), 0, (int)ms.Length);
                var client = new AssemblyAIClient(_assemblyAIToken);
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
