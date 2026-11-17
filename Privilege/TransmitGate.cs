using AgenticSdr.Radio;

namespace AgenticSdr.Privilege;

public enum TransmitBlock
{
    None,
    NoOperator,
    NoClick,
    Privilege,
    NoRadio,
    RadioRxOnly
}

/// <summary>
/// Outcome of the shared transmit policy. <see cref="MayRadiate"/> is true only when every gate passed.
/// </summary>
public sealed record TransmitAuthorization(
    TransmitBlock Block,
    string Reason,
    PrivilegeDecision? Privilege)
{
    public bool MayRadiate => Block == TransmitBlock.None;
}

/// <summary>
/// Shared experimental transmit policy for every mode project. Nothing radiates unless the control
/// operator clicks Transmit, the license covers the frequency and mode, and the open SDR can TX.
/// </summary>
public static class TransmitGate
{
    public static TransmitAuthorization Evaluate(
        OperatorIdentity? op,
        OperatorClick? click,
        AmateurMode mode,
        double frequencyHz,
        double powerWatts,
        string message,
        IRadioDevice? radio,
        bool requireMessageIdentification = true)
    {
        if (click is null)
            return new(TransmitBlock.NoClick, "Transmit only happens when an authorized operator clicks the button.", null);

        if (op is null)
            return new(TransmitBlock.NoOperator, "Sign in as the control operator first.", null);

        if (string.IsNullOrWhiteSpace(message))
            return new(TransmitBlock.Privilege, "There is no message to transmit.", null);

        if (requireMessageIdentification && !CallSign.IsIdentified(message, op.BaseCall))
            return new(TransmitBlock.Privilege, $"The message has to include your call sign ({op.BaseCall}) so the station identifies.", null);

        PrivilegeDecision privilege = Part97.Evaluate(op.License, mode, frequencyHz, powerWatts);
        if (!privilege.Allowed)
            return new(TransmitBlock.Privilege, privilege.Reason, privilege);

        if (radio is null)
            return new(TransmitBlock.NoRadio, "Open a transmit-capable radio before transmitting.", privilege);

        if (!radio.CanTransmit || radio is not IRadioTransmitter)
            return new(
                TransmitBlock.RadioRxOnly,
                $"This radio ({radio.Capabilities.Name}) cannot transmit. Receive-only devices stay receive-only.",
                privilege);

        return new(TransmitBlock.None, privilege.Reason, privilege);
    }
}
