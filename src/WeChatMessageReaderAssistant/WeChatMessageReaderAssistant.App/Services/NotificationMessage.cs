namespace WeChatMessageReaderAssistant.App.Services;

public sealed record NotificationMessage(
    string AppName,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    bool IsProbablyWeChat,
    bool IsVoiceMessage,
    string SpeakText);
