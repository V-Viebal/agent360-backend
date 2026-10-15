using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Users.Abstractions;
using Agent360.UserManagement.UseCase.Users.Commands.ReplaceRoles;
using Agent360.UserManagement.UseCase.Users.Common;
using Xunit;

namespace Agent360.UserManagement.UseCase.UnitTests.Users.Commands.ReplaceRoles;

public sealed class ReplaceUserRolesCommandHandlerTests
{
    [Fact]
    public async Task Handle_ForCurrentUser_ReturnsConflictWithoutPersistence()
    {
        // Arrange
        var userRepository = A.Fake<IRepository<User>>();
        var unitOfWork = A.Fake<IUserManagementUnitOfWork>();
        var currentUser = A.Fake<ICurrentUser>();
        A.CallTo(() => currentUser.UserId).Returns(42);
        var handler = CreateHandler(
            userRepository,
            unitOfWork,
            currentUser: currentUser);

        // Action
        var result = await handler.Handle(
            new ReplaceUserRolesCommand(42, [7], 1),
            TestContext.Current.CancellationToken);

        // Assertion
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be(UserApplicationErrorCodes.CannotAssignOwnRoles);
        A.CallTo(() => unitOfWork.SaveChangesAsync(
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Handle_WithInvalidRoleIds_ReturnsValidationFailure()
    {
        // Arrange
        var user = User.CreateSystemUser(
            "target-user",
            "target@example.test",
            "Target",
            "User");
        var userRepository = A.Fake<IRepository<User>>();
        A.CallTo(() => userRepository.FirstOrDefaultAsync(
                A<Ardalis.Specification.ISpecification<User>>._,
                A<CancellationToken>._))
            .Returns(user);
        var roleRepository = A.Fake<IReadRepository<Role>>();
        A.CallTo(() => roleRepository.ListAsync(
                A<Ardalis.Specification.ISpecification<Role>>._,
                A<CancellationToken>._))
            .Returns([]);
        var handler = CreateHandler(
            userRepository,
            roleRepository: roleRepository);

        // Action
        var result = await handler.Handle(
            new ReplaceUserRolesCommand(user.Id, [99], user.RowVersion),
            TestContext.Current.CancellationToken);

        // Assertion
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be(UserApplicationErrorCodes.InvalidRoleIds);
    }

    [Fact]
    public async Task Handle_WithValidRoles_PersistsAndInvalidatesUserCache()
    {
        // Arrange
        var user = User.CreateSystemUser(
            "target-user",
            "target@example.test",
            "Target",
            "User");
        var customer = Role.Create("Customer", "CUSTOMER");
        var userRepository = A.Fake<IRepository<User>>();
        A.CallTo(() => userRepository.FirstOrDefaultAsync(
                A<Ardalis.Specification.ISpecification<User>>._,
                A<CancellationToken>._))
            .Returns(user);
        var roleRepository = A.Fake<IReadRepository<Role>>();
        A.CallTo(() => roleRepository.ListAsync(
                A<Ardalis.Specification.ISpecification<Role>>._,
                A<CancellationToken>._))
            .Returns([customer]);
        var unitOfWork = A.Fake<IUserManagementUnitOfWork>();
        var invalidator = A.Fake<IPermissionCacheInvalidator>();
        var handler = CreateHandler(
            userRepository,
            unitOfWork,
            roleRepository,
            invalidator);

        // Action
        var result = await handler.Handle(
            new ReplaceUserRolesCommand(
                user.Id,
                [customer.Id],
                user.RowVersion),
            TestContext.Current.CancellationToken);

        // Assertion
        result.IsSuccess.Should().BeTrue();
        result.Value.Roles.Should().ContainSingle()
            .Which.Code.Should().Be("CUSTOMER");
        A.CallTo(() => unitOfWork.SaveChangesAsync(
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => invalidator.InvalidateUserAsync(
            user.Id,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private static ReplaceUserRolesCommandHandler CreateHandler(
        IRepository<User>? userRepository = null,
        IUserManagementUnitOfWork? unitOfWork = null,
        IReadRepository<Role>? roleRepository = null,
        IPermissionCacheInvalidator? invalidator = null,
        ICurrentUser? currentUser = null) =>
        new(
            userRepository ?? A.Fake<IRepository<User>>(),
            roleRepository ?? A.Fake<IReadRepository<Role>>(),
            unitOfWork ?? A.Fake<IUserManagementUnitOfWork>(),
            invalidator ?? A.Fake<IPermissionCacheInvalidator>(),
            A.Fake<IUserPersistenceErrorClassifier>(),
            currentUser ?? A.Fake<ICurrentUser>());
}