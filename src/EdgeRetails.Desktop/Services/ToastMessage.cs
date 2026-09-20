namespace EdgeRetails.Desktop.Services;

public sealed record ToastMessage(
    Guid Id,
    string Message,
    ToastTone Tone);
