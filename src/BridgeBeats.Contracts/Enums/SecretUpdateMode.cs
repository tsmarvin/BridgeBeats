namespace BridgeBeats.Contracts.Enums;

/// <summary>
/// Describes how an application-setting secret should be handled during an update.
/// </summary>
public enum SecretUpdateMode {
    /// <summary>Keep the currently stored value unchanged.</summary>
    Keep,

    /// <summary>Replace the currently stored value with a new secret.</summary>
    Replace,

    /// <summary>Remove the currently stored value.</summary>
    Remove
}
