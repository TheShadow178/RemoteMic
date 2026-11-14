using System.Text.Json.Serialization;

namespace RemoteMic.Core.Mapping;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ShortcutActionStep), "shortcut")]
[JsonDerivedType(typeof(InsertTextActionStep), "insertText")]
[JsonDerivedType(typeof(OpenApplicationActionStep), "openApplication")]
[JsonDerivedType(typeof(FocusInputActionStep), "focusInput")]
[JsonDerivedType(typeof(WaitActionStep), "wait")]
[JsonDerivedType(typeof(VoiceActionStep), "voice")]
public abstract record RemoteActionStep;

public sealed record ShortcutActionStep(RemoteShortcut Shortcut) : RemoteActionStep;
public sealed record InsertTextActionStep(string Text) : RemoteActionStep;
public sealed record OpenApplicationActionStep(RemoteApplication Application) : RemoteActionStep;
public sealed record FocusInputActionStep : RemoteActionStep;
public sealed record WaitActionStep(int Milliseconds) : RemoteActionStep
{
    public int ClampedMilliseconds => Math.Clamp(Milliseconds, 1, 10_000);
}

public sealed record VoiceActionStep : RemoteActionStep;

public sealed record RemoteActionSequence(IReadOnlyList<RemoteActionStep> Steps)
{
    public RemoteActionSequence() : this([]) { }
    public RemoteActionSequence(RemoteShortcut shortcut) : this([new ShortcutActionStep(shortcut)]) { }
    public bool IsEmpty => Steps.Count == 0;

    public string Summary
    {
        get
        {
            if (IsEmpty) return "Multi-step action…";
            var shown = Steps.Take(2).Select(SummarizeStep).ToArray();
            var text = string.Join(" → ", shown);
            return Steps.Count > shown.Length ? $"{text} +{Steps.Count - shown.Length}" : text;
        }
    }

    private static string SummarizeStep(RemoteActionStep step) => step switch
    {
        ShortcutActionStep shortcut => shortcut.Shortcut.Label,
        InsertTextActionStep text => Preview(text.Text),
        OpenApplicationActionStep app => app.Application.Label,
        FocusInputActionStep => "Focus input",
        WaitActionStep wait => $"Wait {wait.ClampedMilliseconds / 1000d:0.#}s",
        VoiceActionStep => "Voice",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    private static string Preview(string text)
    {
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flattened.Length == 0) return "Empty text";
        return flattened.Length > 12 ? flattened[..12] + "…" : flattened;
    }
}

public enum VoiceStepEndReason
{
    RecordingStopped,
    Timeout,
    NeverStarted,
}

public readonly record struct VoiceStepDecision(bool Finished, VoiceStepEndReason? Reason)
{
    public static VoiceStepDecision Wait { get; } = new(false, null);
    public static VoiceStepDecision Finish(VoiceStepEndReason reason) => new(true, reason);
}

/// <summary>Pure state machine for the "wait for voice input" action step.</summary>
public sealed class VoiceStepWatcher(DateTimeOffset beganAt)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(2);

    private bool _sawRecording;
    private VoiceStepEndReason? _endReason;

    public VoiceStepDecision Update(bool recording, DateTimeOffset now)
    {
        if (_endReason is { } ended) return VoiceStepDecision.Finish(ended);
        if (recording) _sawRecording = true;
        if (_sawRecording && !recording)
            return Finish(VoiceStepEndReason.RecordingStopped);

        var elapsed = now - beganAt;
        if (elapsed >= Timeout) return Finish(VoiceStepEndReason.Timeout);
        if (!_sawRecording && elapsed >= StartGrace)
            return Finish(VoiceStepEndReason.NeverStarted);
        return VoiceStepDecision.Wait;
    }

    private VoiceStepDecision Finish(VoiceStepEndReason reason)
    {
        _endReason = reason;
        return VoiceStepDecision.Finish(reason);
    }
}
