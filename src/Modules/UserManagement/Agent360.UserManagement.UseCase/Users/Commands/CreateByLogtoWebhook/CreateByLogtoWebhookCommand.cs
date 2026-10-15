using Hmz.Core.SharedKernel.Messaging;

namespace Agent360.UserManagement.UseCase.Users.Commands.CreateByLogtoWebhook;

public sealed record CreateByLogtoWebhookCommand(
    string LogtoUserId,
    string Email,
    string FirstName,
    string LastName,
    string? AvatarUrl) : ICommand;