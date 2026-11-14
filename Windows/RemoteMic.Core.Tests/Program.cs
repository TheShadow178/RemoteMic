using System.Text.Json;
using RemoteMic.Core.Atvv;
using RemoteMic.Core.Audio;
using RemoteMic.Core.Input;
using RemoteMic.Core.Mapping;
using RemoteMic.Core.Voice;

var tests = new (string Name, Action Body)[]
{
    ("ATVV capability parsing and commands", TestAtvvCapabilities),
    ("ATVV stream reset and sync ordering", TestAtvvStreamState),
    ("ADPCM clamps invalid step index", TestAdpcm),
    ("gesture fast path", TestGestureFastPath),
    ("gesture single, double, long and cancellation", TestGestures),
    ("Chromecast Windows HID layouts", TestHidNormalizer),
    ("X6 Windows HID collections", TestX6HidNormalizer),
    ("X6 voice gesture reconstruction", TestX6VoiceGestures),
    ("remote profiles and Windows defaults", TestMappings),
    ("voice session coordination", TestVoiceSessionCoordinator),
    ("action serialization and voice watcher", TestActions),
    ("remote-priority audio routing", TestAudioRouting),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {name}\n{error}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} core self-tests passed.");
return failures == 0 ? 0 : 1;

static void TestAtvvCapabilities()
{
    Assert.True(AtvvProtocol.TryParseCapabilities(
        [0x0B, 0x01, 0x00, 0x03, 0x01, 0x00, 0x78], out var v10));
    Assert.Equal(AtvvVersion.V10, v10!.Version);
    Assert.Equal<AtvvCodec?>(AtvvCodec.Adpcm16Khz, v10.SelectedCodec);
    Assert.Equal(120, v10.FrameSize);

    var protocol = new AtvvProtocol();
    protocol.AcceptCapabilities(v10);
    Assert.SequenceEqual(new byte[] { 0x0C, 0x00 }, protocol.CreateMicrophoneOpenCommand());
    Assert.SequenceEqual(new byte[] { 0x0D, 0x2A }, protocol.CreateMicrophoneCloseCommand(0x2A));
    Assert.SequenceEqual(new byte[] { 0x0E, 0x2A }, protocol.CreateKeepAliveCommand(0x2A));
    var start = Assert.Is<AtvvControlEvent.AudioStart>(
        protocol.ParseControl([0x04, 0x01, 0x02, 0x33]));
    Assert.Equal((byte)0x33, start.StreamId);

    Assert.True(AtvvProtocol.TryParseCapabilities(
        [0x0B, 0x00, 0x04, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00], out var v04));
    protocol.AcceptCapabilities(v04!);
    Assert.SequenceEqual(new byte[] { 0x0C, 0x00, 0x01 }, protocol.CreateMicrophoneOpenCommand());
    Assert.SequenceEqual(new byte[] { 0x0D }, protocol.CreateMicrophoneCloseCommand(9));
}

static void TestAtvvStreamState()
{
    var protocol = new AtvvProtocol();
    protocol.AcceptCapabilities(new AtvvCapabilities(AtvvVersion.V10, 0x02, 0, 120));
    byte[] packet = [0x12, 0x34, 0xAB, 0xCD, 0x56, 0x78];
    protocol.BeginAudioStream(AtvvCodec.Adpcm16Khz);
    var first = protocol.DecodeAudio(packet)!.Value;
    _ = protocol.DecodeAudio(Enumerable.Repeat((byte)0x77, 24).ToArray());
    protocol.EndAudioStream();
    protocol.BeginAudioStream(AtvvCodec.Adpcm16Khz);
    var second = protocol.DecodeAudio(packet)!.Value;
    Assert.SequenceEqual(first.Samples, second.Samples);
    Assert.Equal((ushort)0, second.Sequence);

    protocol.PrepareForAudioStream();
    protocol.ApplyAudioSync(AtvvCodec.Adpcm16Khz, 42, 1_234, 20);
    protocol.BeginAudioStream(AtvvCodec.Adpcm16Khz);
    var synced = protocol.DecodeAudio([0x12, 0x34])!.Value;
    Assert.Equal((ushort)42, synced.Sequence);
    Assert.NotEqual(first.Samples[0], synced.Samples[0]);

    protocol.ApplyAudioSync(AtvvCodec.Adpcm16Khz, 70, -2_000, 30);
    _ = protocol.DecodeAudio([0x77, 0x77]);
    protocol.EndAudioStream();
    protocol.BeginAudioStream(AtvvCodec.Adpcm16Khz);
    var afterActiveSync = protocol.DecodeAudio(packet)!.Value;
    Assert.SequenceEqual(first.Samples, afterActiveSync.Samples);
    Assert.Equal((ushort)0, afterActiveSync.Sequence);
}

static void TestAdpcm()
{
    var decoder = new AdpcmDecoder();
    decoder.Reset(0, byte.MaxValue);
    var samples = decoder.Decode([0x77, 0xFF]);
    Assert.Equal(4, samples.Length);
    Assert.Equal(short.MinValue, samples[^1]);
}

static void TestGestureFastPath()
{
    var recognizer = new RemoteGestureRecognizer();
    var events = new List<RemoteGestureEvent>();
    recognizer.Gesture += events.Add;
    var now = DateTimeOffset.UnixEpoch;
    recognizer.Handle("03", true, now);
    recognizer.Handle("03", false, now + TimeSpan.FromSeconds(2));
    Assert.SequenceEqual(
        new[]
        {
            new RemoteGestureEvent("03", RemoteGesture.Single, true),
            new RemoteGestureEvent("03", RemoteGesture.Single, false),
        }, events);
}

static void TestGestures()
{
    var enabled = new HashSet<RemoteGesture> { RemoteGesture.Double, RemoteGesture.Long };
    var recognizer = new RemoteGestureRecognizer
    {
        IsGestureEnabled = _ => true,
        IsDoubleEnabled = _ => enabled.Contains(RemoteGesture.Double),
        IsLongEnabled = _ => enabled.Contains(RemoteGesture.Long),
    };
    var events = new List<RemoteGestureEvent>();
    recognizer.Gesture += events.Add;
    var start = DateTimeOffset.UnixEpoch;

    recognizer.Handle("03", true, start);
    recognizer.Handle("03", false, start + TimeSpan.FromMilliseconds(50));
    recognizer.Advance(start + TimeSpan.FromMilliseconds(329));
    Assert.Equal(0, events.Count);
    recognizer.Advance(start + TimeSpan.FromMilliseconds(330));
    AssertGesturePulse(events, RemoteGesture.Single);

    events.Clear();
    var second = start + TimeSpan.FromSeconds(1);
    recognizer.Handle("03", true, second);
    recognizer.Handle("03", false, second + TimeSpan.FromMilliseconds(20));
    recognizer.Handle("03", true, second + TimeSpan.FromMilliseconds(100));
    recognizer.Handle("03", false, second + TimeSpan.FromMilliseconds(120));
    AssertGesturePulse(events, RemoteGesture.Double);

    events.Clear();
    var third = start + TimeSpan.FromSeconds(2);
    recognizer.Handle("03", true, third);
    recognizer.Advance(third + TimeSpan.FromMilliseconds(600));
    recognizer.Handle("03", false, third + TimeSpan.FromMilliseconds(650));
    AssertGesturePulse(events, RemoteGesture.Long);

    events.Clear();
    var fourth = start + TimeSpan.FromSeconds(3);
    recognizer.Handle("03", true, fourth);
    recognizer.Advance(fourth + TimeSpan.FromMilliseconds(600));
    recognizer.CancelPending();
    AssertGesturePulse(events, RemoteGesture.Long);
}

static void TestHidNormalizer()
{
    var normalizer = new ChromecastHidReportNormalizer();
    var usages = new (byte Low, byte High, string Id)[]
    {
        (0x9E, 0x01, "01"), (0x89, 0x01, "11"), (0x42, 0x00, "03"),
        (0x43, 0x00, "04"), (0x44, 0x00, "05"), (0x45, 0x00, "06"),
        (0x41, 0x00, "07"), (0x24, 0x02, "0B"), (0x23, 0x02, "0A"),
        (0xE9, 0x00, "0C"), (0xEA, 0x00, "0D"), (0xE2, 0x00, "08"),
        (0x77, 0x00, "0E"), (0x78, 0x00, "0F"),
    };
    foreach (var usage in usages)
    {
        Assert.True(normalizer.TryNormalize([0x02, usage.Low, usage.High], out var down));
        Assert.Equal(usage.Id, down.ButtonId);
        Assert.True(down.IsDown);
        Assert.True(normalizer.TryNormalize([0x02, 0x00, 0x00], out var up));
        Assert.Equal(usage.Id, up.ButtonId);
        Assert.False(up.IsDown);
    }

    // The REV0110 Windows filter rewrites the remote's eight-byte direct
    // report into the three-byte descriptor format while retaining report ID 1.
    Assert.True(normalizer.TryNormalize([0x01, 0x42, 0x00], out var translatedDown));
    Assert.Equal("03", translatedDown.ButtonId);
    Assert.True(translatedDown.IsDown);
    Assert.True(normalizer.TryNormalize([0x01, 0x00, 0x00], out var translatedUp));
    Assert.False(translatedUp.IsDown);

    Assert.True(normalizer.TryNormalize([0x01, 0x03, 0, 0, 0, 0, 0, 0], out var directDown));
    Assert.Equal("03", directDown.ButtonId);
    Assert.True(normalizer.TryNormalize([0x01, 0, 0, 0, 0, 0, 0, 0], out var directUp));
    Assert.False(directUp.IsDown);
}

static void TestX6HidNormalizer()
{
    var normalizer = new X6HidReportNormalizer();

    var keyboardDown = normalizer.Normalize([0x01, 0x00, 0x00, 0x52, 0x00]);
    Assert.Equal(1, keyboardDown.Count);
    Assert.Equal(new HidButtonEvent("k52", true, 0x52), keyboardDown[0]);
    Assert.Equal(0, normalizer.Normalize([0x01, 0x00, 0x00, 0x52, 0x00]).Count);
    var keyboardUp = normalizer.Normalize([0x01, 0x00, 0x00, 0x00, 0x00]);
    Assert.Equal(new HidButtonEvent("k52", false, 0x52), keyboardUp.Single());

    var consumerDown = normalizer.Normalize([0x02, 0xE9, 0x00]);
    Assert.Equal(new HidButtonEvent("cE9", true, 0xE9), consumerDown.Single());
    var consumerUp = normalizer.Normalize([0x02, 0x00, 0x00]);
    Assert.Equal(new HidButtonEvent("cE9", false, 0xE9), consumerUp.Single());

    Assert.Equal(0, normalizer.Normalize([0x02, 0x21, 0x02]).Count);
    Assert.Equal(0, normalizer.Reset().Count);

    var systemDown = normalizer.Normalize([0x03, 0x01]);
    Assert.Equal(new HidButtonEvent("s01", true, 0x01), systemDown.Single());
    var reset = normalizer.Reset();
    Assert.Equal(new HidButtonEvent("s01", false, 0x01), reset.Single());
    Assert.Equal(0, normalizer.Normalize([0x04, 0x01]).Count);
}

static void TestX6VoiceGestures()
{
    var recognizer = new X6VoiceGestureRecognizer();
    var shortPresses = 0;
    var longReleases = 0;
    recognizer.ShortPressed += () => shortPresses++;
    recognizer.LongPressEnded += () => longReleases++;
    var start = DateTimeOffset.UnixEpoch;

    recognizer.Handle([0x02, 0x21, 0x02], start);
    recognizer.Handle([0x02, 0x00, 0x00], start + TimeSpan.FromMilliseconds(20));
    recognizer.Advance(start + TimeSpan.FromMilliseconds(139));
    Assert.Equal(0, shortPresses);
    recognizer.Advance(start + TimeSpan.FromMilliseconds(140));
    Assert.Equal(1, shortPresses);

    recognizer.Handle([0x02, 0x21, 0x02], start + TimeSpan.FromSeconds(1));
    recognizer.Handle([0x02, 0x00, 0x00], start + TimeSpan.FromMilliseconds(1_020));
    recognizer.Handle([0x01, 0x00, 0x00, 0xAA], start + TimeSpan.FromMilliseconds(1_100));
    recognizer.Advance(start + TimeSpan.FromMilliseconds(1_380));
    recognizer.Handle([0x01, 0x00, 0x00, 0x00], start + TimeSpan.FromMilliseconds(1_400));
    Assert.Equal(1, shortPresses);
    Assert.Equal(1, longReleases);
}

static void TestMappings()
{
    Assert.Equal(15, RemoteProfiles.ChromecastButtons.Count);
    Assert.Equal(17, RemoteProfiles.X6Buttons.Count);
    var defaults = RemoteMappingDefaults.CreateWindows();
    var left = defaults[new(SupportedRemoteId.Chromecast, "05", RemoteGesture.Single)];
    Assert.Equal(RemoteMappingTarget.CustomShortcut, left.Target);
    Assert.Equal(KeyModifiers.Control | KeyModifiers.Windows, left.Shortcut!.Modifiers);
    var selectLong = defaults[new(SupportedRemoteId.Chromecast, "07", RemoteGesture.Long)];
    Assert.Equal(RemoteMappingTarget.Space, selectLong.Target);
}

static void TestVoiceSessionCoordinator()
{
    var start = DateTimeOffset.UnixEpoch;
    var coordinator = new VoiceSessionCoordinator();
    var commands = new List<VoiceSessionCommand>();
    coordinator.CommandIssued += commands.Add;

    coordinator.RemoteAudioStarted(
        SupportedRemoteId.Chromecast,
        0x03,
        start,
        supportsPhysicalHoldGesture: true);
    Assert.Is<VoiceSessionCommand.TriggerTap>(commands.Single());
    coordinator.TargetRecordingChanged(true);
    commands.Clear();
    coordinator.RemoteAudioStopped(
        SupportedRemoteId.Chromecast,
        0x02,
        start + TimeSpan.FromMilliseconds(180));
    var reopen = Assert.Is<VoiceSessionCommand.OpenRemote>(commands.Single());
    Assert.Equal(SupportedRemoteId.Chromecast, reopen.Remote);
    Assert.True(reopen.BypassDebounce);

    commands.Clear();
    coordinator.RemoteAudioStarted(
        SupportedRemoteId.Chromecast,
        0x00,
        start + TimeSpan.FromMilliseconds(200),
        supportsPhysicalHoldGesture: true);
    coordinator.RemoteAudioStarted(
        SupportedRemoteId.Chromecast,
        0x03,
        start + TimeSpan.FromSeconds(1),
        supportsPhysicalHoldGesture: true);
    coordinator.RemoteAudioStopped(
        SupportedRemoteId.Chromecast,
        0x02,
        start + TimeSpan.FromMilliseconds(1_160));
    Assert.Equal(2, commands.Count);
    Assert.Is<VoiceSessionCommand.TriggerTap>(commands[0]);
    Assert.Is<VoiceSessionCommand.CloseAllRemotes>(commands[1]);
    Assert.Equal(VoiceSessionPhase.Closing, coordinator.Phase);

    var stale = new VoiceSessionCoordinator();
    var staleCommands = new List<VoiceSessionCommand>();
    stale.CommandIssued += staleCommands.Add;
    stale.RemoteAudioStarted(
        SupportedRemoteId.Chromecast,
        0x00,
        start,
        supportsPhysicalHoldGesture: true);
    Assert.Is<VoiceSessionCommand.CloseAllRemotes>(staleCommands.Single());
    Assert.Equal(VoiceSessionPhase.Closed, stale.Phase);

    var failedOpen = new VoiceSessionCoordinator();
    var failedOpenCommands = new List<VoiceSessionCommand>();
    failedOpen.CommandIssued += failedOpenCommands.Add;
    failedOpen.RemoteAudioStarted(
        SupportedRemoteId.Chromecast,
        0x03,
        start,
        supportsPhysicalHoldGesture: true);
    failedOpenCommands.Clear();
    failedOpen.TargetRecordingChanged(false);
    Assert.Equal(VoiceSessionPhase.Closed, failedOpen.Phase);
    Assert.Is<VoiceSessionCommand.CloseAllRemotes>(failedOpenCommands.Single());

    var x6 = new VoiceSessionCoordinator();
    var x6Commands = new List<VoiceSessionCommand>();
    x6.CommandIssued += x6Commands.Add;
    x6.RemoteAudioStarted(SupportedRemoteId.X6, 0x00, start, false);
    Assert.Is<VoiceSessionCommand.TriggerTap>(x6Commands.Single());
    x6.TargetRecordingChanged(true);
    x6Commands.Clear();
    x6.RemoteHidShortPress(SupportedRemoteId.X6, start + TimeSpan.FromMilliseconds(200));
    Assert.Equal(0, x6Commands.Count);
    x6.RemoteHidShortPress(SupportedRemoteId.X6, start + TimeSpan.FromSeconds(2));
    Assert.Equal(2, x6Commands.Count);
    Assert.Is<VoiceSessionCommand.TriggerTap>(x6Commands[0]);
    Assert.Is<VoiceSessionCommand.CloseAllRemotes>(x6Commands[1]);

    var twoRemotes = new VoiceSessionCoordinator();
    twoRemotes.RemoteAudioStarted(SupportedRemoteId.X6, 0x00, start, false);
    twoRemotes.RemoteAudioStarted(SupportedRemoteId.Chromecast, 0x03, start, true);
    Assert.Equal(2, twoRemotes.StreamingRemotes.Count);
    twoRemotes.RemoteAudioStopped(SupportedRemoteId.X6, 0x00, start);
    Assert.Equal(1, twoRemotes.StreamingRemotes.Count);
    Assert.True(twoRemotes.StreamingRemotes.Contains(SupportedRemoteId.Chromecast));
}

static void TestActions()
{
    var sequence = new RemoteActionSequence(
    [
        new ShortcutActionStep(new RemoteShortcut(0x43, KeyModifiers.Control, "Ctrl+C")),
        new InsertTextActionStep("hello\nworld with a longer preview"),
        new WaitActionStep(20_000),
        new VoiceActionStep(),
    ]);
    Assert.True(sequence.Summary.Contains("+2", StringComparison.Ordinal));
    Assert.Equal(10_000, ((WaitActionStep)sequence.Steps[2]).ClampedMilliseconds);
    var json = JsonSerializer.Serialize(sequence);
    var decoded = JsonSerializer.Deserialize<RemoteActionSequence>(json);
    Assert.Equal(sequence.Steps.Count, decoded!.Steps.Count);
    Assert.Is<ShortcutActionStep>(decoded.Steps[0]);
    Assert.Is<VoiceActionStep>(decoded.Steps[^1]);

    var start = DateTimeOffset.UnixEpoch;
    var watcher = new VoiceStepWatcher(start);
    Assert.False(watcher.Update(false, start + TimeSpan.FromSeconds(1)).Finished);
    Assert.False(watcher.Update(true, start + TimeSpan.FromSeconds(1.1)).Finished);
    var stopped = watcher.Update(false, start + TimeSpan.FromSeconds(1.2));
    Assert.Equal(VoiceStepEndReason.RecordingStopped, stopped.Reason);

    var never = new VoiceStepWatcher(start).Update(false, start + TimeSpan.FromSeconds(2));
    Assert.Equal(VoiceStepEndReason.NeverStarted, never.Reason);
}

static void TestAudioRouting()
{
    var router = new AudioRoutePolicy();
    router.FeedLocal([0.2f], AudioRoutePolicy.OutputSampleRate);
    Assert.Equal(AudioRouteSource.Silence, router.Render(1).Source);

    router.SetActive(true);
    router.FeedLocal([0.1f, 0.2f, 0.3f], AudioRoutePolicy.OutputSampleRate);
    var local = router.Render(3);
    Assert.Equal(AudioRouteSource.LocalMicrophone, local.Source);
    Assert.SequenceNear(new[] { 0.1f, 0.2f, 0.3f }, local.MonoSamples);

    router.FeedLocal(Enumerable.Repeat(0.25f, 6_000).ToArray(), AudioRoutePolicy.OutputSampleRate);
    router.FeedRemote([1_638, 1_638], AudioRoutePolicy.OutputSampleRate);
    var remote = router.Render(4);
    Assert.Equal(AudioRouteSource.RemoteMicrophone, remote.Source);
    Assert.True(remote.MonoSamples[0] > 0.49f);
    Assert.Near(0.25f, remote.MonoSamples[2]);

    router.FeedLocal(Enumerable.Repeat(0.1f, 20_000).ToArray(), AudioRoutePolicy.OutputSampleRate);
    router.FeedRemote([1_000], AudioRoutePolicy.OutputSampleRate);
    _ = router.Render(1);
    Assert.True(router.PendingLocalFrames <= AudioRoutePolicy.FallbackBacklogFrames);

    var upsampled = AudioRoutePolicy.Resample([0f, 1f], 16_000, 48_000);
    Assert.Equal(6, upsampled.Length);
}

static void AssertGesturePulse(IReadOnlyList<RemoteGestureEvent> events, RemoteGesture gesture)
{
    Assert.Equal(2, events.Count);
    Assert.Equal(gesture, events[0].Gesture);
    Assert.True(events[0].IsDown);
    Assert.False(events[1].IsDown);
}

static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    public static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new InvalidOperationException($"Did not expect {actual}.");
    }

    public static T Is<T>(object value) where T : class => value as T ??
        throw new InvalidOperationException($"Expected {typeof(T).Name}; got {value.GetType().Name}.");

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Sequences are different.");
    }

    public static void Near(float expected, float actual, float tolerance = 0.0001f)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected about {expected}; got {actual}.");
    }

    public static void SequenceNear(IEnumerable<float> expected, IEnumerable<float> actual)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        Equal(expectedArray.Length, actualArray.Length);
        for (var i = 0; i < expectedArray.Length; i++) Near(expectedArray[i], actualArray[i]);
    }
}
