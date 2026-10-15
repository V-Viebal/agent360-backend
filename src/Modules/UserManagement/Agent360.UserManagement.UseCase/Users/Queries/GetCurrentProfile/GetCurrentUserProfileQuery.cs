using Hmz.Core.SharedKernel.Messaging;

namespace Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;

public sealed record GetCurrentUserProfileQuery(long UserId)
    : IQuery<CurrentUserProfileResponse?>;