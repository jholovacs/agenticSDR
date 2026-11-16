namespace AgenticSdr.Station;

/// <summary>One outbound message waiting for the control operator. Drafting it does not transmit.</summary>
public sealed record DraftMessage(
    string Id,
    string Text,
    double FrequencyHz,
    StationMode Mode,
    double PowerWatts,
    DraftSource Source,
    DateTimeOffset CreatedAt,
    RepeaterAccess? Repeater = null);

/// <summary>A draft plus the privilege decision for the operator who is signed in right now.</summary>
public sealed record DraftView(
    string Id,
    string Text,
    double FrequencyHz,
    StationMode Mode,
    double PowerWatts,
    DraftSource Source,
    DateTimeOffset CreatedAt,
    bool Allowed,
    bool WithinLicense,
    string Reason,
    string? Band,
    string? Caution,
    RepeaterAccess? Repeater = null);

/// <summary>Text the station copied. This is receive-side. It does not authorize a transmission.</summary>
public sealed record HeardNote(
    string Id,
    string Text,
    double? FrequencyHz,
    StationMode? Mode,
    DraftSource Source,
    DateTimeOffset HeardAt);

/// <summary>
/// A transmission the control operator authorized by clicking Transmit.
/// Radiated is false: this desk records the authorization and does not key a radio.
/// </summary>
public sealed record AuthorizedTransmission(
    string Id,
    string CallSign,
    string BaseCall,
    LicenseClass License,
    string Text,
    double FrequencyHz,
    StationMode Mode,
    double PowerWatts,
    string Reason,
    DateTimeOffset AuthorizedAt,
    RepeaterAccess? Repeater = null)
{
    public bool Radiated => false;
}

public sealed record AuthorizeResult(bool Authorized, string Reason, AuthorizedTransmission? Transmission);

public sealed record StationSnapshot(
    OperatorIdentity? Operator,
    string? LicenseSummary,
    IReadOnlyList<DraftView> Drafts,
    IReadOnlyList<HeardNote> Heard,
    IReadOnlyList<AuthorizedTransmission> Authorized);

/// <summary>
/// The control-operator desk. Agents may draft text and log what was heard.
/// <see cref="Authorize"/> runs only when the page button produces an <see cref="OperatorClick"/>.
/// </summary>
public sealed class StationDesk
{
    const int MaxDrafts = 30;
    const int MaxNotes = 40;
    const int MaxAuthorized = 40;
    const int MaxText = 2_000;

    readonly object gate = new();
    readonly List<DraftMessage> drafts = [];
    readonly List<HeardNote> heard = [];
    readonly List<AuthorizedTransmission> authorized = [];
    OperatorIdentity? control;

    public event EventHandler<AuthorizedTransmission>? TransmissionAuthorized;

    public OperatorIdentity? CopyOperator()
    {
        lock (gate)
            return control;
    }

    public void SignIn(string callsign, LicenseClass license, string? grid = null)
    {
        if (!CallSign.TryNormalize(callsign, out string display, out string baseCall))
            throw new ArgumentException("Enter a US amateur call sign, such as W1AW or KA1ABC. A portable indicator like W1AW/7 is allowed.");

        GridPlace? place = StationGrid.Optional(grid);
        var identity = new OperatorIdentity(
            display,
            baseCall,
            license,
            place?.Grid,
            place?.Latitude,
            place?.Longitude,
            place?.Place,
            place?.Coordinates);

        lock (gate)
            control = identity;
    }

    public void SignOut()
    {
        lock (gate)
            control = null;
    }

    public DraftView Draft(string text, double frequencyHz, StationMode mode, double powerWatts, DraftSource source, RepeaterAccess? repeater = null)
    {
        string body = CleanText(text);
        if (!double.IsFinite(powerWatts))
            throw new ArgumentException("Power has to be a number of watts.");

        if (repeater is not null)
        {
            if (mode != StationMode.Nbfm)
                throw new ArgumentException("Repeater phone uses FM phone (nbfm).");
            frequencyHz = RequireFrequency(repeater.InputFrequencyHz);
        }
        else
        {
            frequencyHz = RequireFrequency(frequencyHz);
        }

        var draft = new DraftMessage(
            NewId(),
            body,
            frequencyHz,
            mode,
            powerWatts,
            source,
            DateTimeOffset.Now,
            repeater);

        lock (gate)
        {
            if (control == null)
                throw new ArgumentException("Set your call sign and license class on the station page before drafting a message.");

            // Repeater phone is judged on the transmit (input) frequency. Do not hold a machine the license cannot use.
            if (repeater is not null)
            {
                PrivilegeDecision radio = StationRepeater.Evaluate(control.License, repeater, powerWatts);
                if (!radio.WithinLicense)
                    throw new ArgumentException(radio.Reason);
            }

            drafts.Add(draft);
            while (drafts.Count > MaxDrafts)
                drafts.RemoveAt(0);
            return Judge(draft, control);
        }
    }

    public void Discard(string draftId)
    {
        lock (gate)
        {
            int index = drafts.FindIndex(item => item.Id == draftId);
            if (index < 0)
                throw new ArgumentException("That draft is no longer waiting.");
            drafts.RemoveAt(index);
        }
    }

    public HeardNote NoteHeard(string text, double? frequencyHz, StationMode? mode, DraftSource source)
    {
        string body = CleanText(text);
        if (frequencyHz is { } hz)
            frequencyHz = RequireFrequency(hz);

        var note = new HeardNote(NewId(), body, frequencyHz, mode, source, DateTimeOffset.Now);
        lock (gate)
        {
            if (control == null)
                throw new ArgumentException("Set your call sign and license class on the station page before logging copy.");
            heard.Add(note);
            while (heard.Count > MaxNotes)
                heard.RemoveAt(0);
            return note;
        }
    }

    /// <summary>
    /// Authorize one draft. The click has to be the one the transmit button just created for this draft.
    /// The license, the call sign in the text, and the power are checked again at this moment.
    /// </summary>
    public AuthorizeResult Authorize(string draftId, OperatorClick click)
    {
        if (click.DraftId != draftId)
            return new AuthorizeResult(false, "Transmit runs only when you check the box and click Transmit on that draft.", null);

        AuthorizedTransmission? sent = null;
        lock (gate)
        {
            if (control == null)
                return new AuthorizeResult(false, "Set your call sign and license class before transmitting.", null);

            int index = drafts.FindIndex(item => item.Id == draftId);
            if (index < 0)
                return new AuthorizeResult(false, "That draft is no longer waiting.", null);

            DraftMessage draft = drafts[index];
            DraftView judged = Judge(draft, control);
            if (!judged.Allowed)
                return new AuthorizeResult(false, judged.Reason, null);

            drafts.RemoveAt(index);
            sent = new AuthorizedTransmission(
                NewId(),
                control.CallSign,
                control.BaseCall,
                control.License,
                draft.Text,
                draft.FrequencyHz,
                draft.Mode,
                draft.PowerWatts,
                judged.Reason,
                DateTimeOffset.Now,
                draft.Repeater);
            authorized.Add(sent);
            while (authorized.Count > MaxAuthorized)
                authorized.RemoveAt(0);
        }

        TransmissionAuthorized?.Invoke(this, sent!);
        return new AuthorizeResult(true, "Authorized. This desk does not key a radio.", sent);
    }

    public StationSnapshot Snapshot()
    {
        lock (gate)
        {
            OperatorIdentity? op = control;
            var views = new List<DraftView>(drafts.Count);
            foreach (DraftMessage draft in drafts)
                views.Add(Judge(draft, op));

            return new StationSnapshot(
                op,
                op == null ? null : Part97.Summary(op.License),
                views,
                heard.ToArray(),
                authorized.ToArray());
        }
    }

    public AuthorizedTransmission? FindAuthorized(string id)
    {
        lock (gate)
            return authorized.Find(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    static DraftView Judge(DraftMessage draft, OperatorIdentity? op)
    {
        if (op == null)
        {
            return View(draft, new PrivilegeDecision(
                false,
                false,
                "Set your call sign and license class on the station page. Nothing can be authorized until you do.",
                null,
                null,
                null));
        }

        PrivilegeDecision radio = draft.Repeater is { } repeater
            ? StationRepeater.Evaluate(op.License, repeater, draft.PowerWatts)
            : Part97.Evaluate(op.License, draft.Mode, draft.FrequencyHz, draft.PowerWatts);
        if (!radio.WithinLicense)
            return View(draft, radio);

        if (!CallSign.IsIdentified(draft.Text, op.BaseCall))
        {
            return View(draft, radio with
            {
                Allowed = false,
                Reason = $"The message has to include your call sign {op.BaseCall}. Part 97 requires the station to identify."
            });
        }

        return View(draft, radio);
    }

    static DraftView View(DraftMessage draft, PrivilegeDecision decision) => new(
        draft.Id,
        draft.Text,
        draft.FrequencyHz,
        draft.Mode,
        draft.PowerWatts,
        draft.Source,
        draft.CreatedAt,
        decision.Allowed,
        decision.WithinLicense,
        decision.Reason,
        decision.Band,
        decision.Caution,
        draft.Repeater);

    static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("The message is empty.");
        string body = text.Trim();
        if (body.Length > MaxText)
            throw new ArgumentException($"The message is longer than {MaxText} characters.");
        return body;
    }

    static double RequireFrequency(double frequencyHz)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0)
            throw new ArgumentException("Frequency has to be a positive number of hertz.");
        return Math.Round(frequencyHz);
    }

    static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
