using AgenticSdr.Privilege;

namespace AgenticSdr.Cw.Chat;

/// <summary>
/// Part 97-style CW identification: callsign at least every 10 minutes, and on sign-off.
/// </summary>
public sealed class CwIdTracker
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    DateTimeOffset? _lastIdentified;

    public bool HasFreshId =>
        _lastIdentified is { } at && DateTimeOffset.UtcNow - at < Period;

    /// <summary>True after any identifying transmission this desk session.</summary>
    public bool HasIdentifiedThisSession => _lastIdentified is not null;

    public bool IsDue => !HasFreshId;

    public TimeSpan? UntilDue
    {
        get
        {
            if (_lastIdentified is null)
                return TimeSpan.Zero;
            TimeSpan left = Period - (DateTimeOffset.UtcNow - _lastIdentified.Value);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>Uppercase the text and append DE {call} when identification is due.</summary>
    public string PrepareOutgoing(string? text, OperatorIdentity op)
    {
        ArgumentNullException.ThrowIfNull(op);
        string body = (text ?? "").Trim().ToUpperInvariant();
        if (!IsDue)
            return body;

        if (CallSign.IsIdentified(body, op.BaseCall))
            return body;

        return string.IsNullOrEmpty(body)
            ? $"DE {op.CallSign}"
            : $"{body} DE {op.CallSign}";
    }

    public void NoteTransmitted(string text, OperatorIdentity op)
    {
        if (CallSign.IsIdentified(text, op.BaseCall))
            _lastIdentified = DateTimeOffset.UtcNow;
    }

    public string BuildSignOff(OperatorIdentity op, string prosign)
    {
        ArgumentNullException.ThrowIfNull(op);
        string mark = NormalizeProsign(prosign);
        return $"DE {op.CallSign} {mark}";
    }

    public static string NormalizeProsign(string? prosign)
    {
        string key = (prosign ?? "").Trim().ToUpperInvariant();
        return key switch
        {
            "SK" or "QRT" or "CL" or "QSY" => key,
            _ => throw new ArgumentException("Prosign must be SK, CL, QRT, or QSY.")
        };
    }

    public object Status() => new
    {
        due = IsDue,
        hasFreshId = HasFreshId,
        hasIdentifiedThisSession = HasIdentifiedThisSession,
        periodMinutes = Period.TotalMinutes,
        secondsUntilDue = UntilDue?.TotalSeconds
    };
}
