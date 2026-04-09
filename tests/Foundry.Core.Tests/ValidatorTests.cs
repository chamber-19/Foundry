using Foundry.Broker;
using Foundry.Services;
using Xunit;
using System.Collections.Generic;

namespace Foundry.Core.Tests;

public class ChatValidatorTests
{
    private readonly ChatRouteRequestValidator _routeValidator = new();
    private readonly ChatSendRequestValidator _sendValidator = new();

    // ChatRouteRequestValidator — valid routes

    [Theory]
    [InlineData("chief")]
    [InlineData("engineering")]
    [InlineData("suite")]
    [InlineData("business")]
    [InlineData("ml")]
    public void ChatRouteRequestValidator_KnownRoute_IsValid(string route)
    {
        var result = _routeValidator.Validate(new ChatRouteRequest(route));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("CHIEF")]
    [InlineData("Engineering")]
    [InlineData("ML")]
    public void ChatRouteRequestValidator_KnownRouteMixedCase_IsValid(string route)
    {
        var result = _routeValidator.Validate(new ChatRouteRequest(route));
        Assert.True(result.IsValid);
    }

    // ChatRouteRequestValidator — invalid: empty / whitespace

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatRouteRequestValidator_EmptyOrWhitespaceRoute_FailsWithRequiredMessage(string route)
    {
        var result = _routeValidator.Validate(new ChatRouteRequest(route));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Route is required.");
    }

    // ChatRouteRequestValidator — invalid: unknown route

    [Theory]
    [InlineData("unknown")]
    [InlineData("admin")]
    [InlineData("desk")]
    public void ChatRouteRequestValidator_UnknownRoute_FailsWithKnownRoutesMessage(string route)
    {
        var result = _routeValidator.Validate(new ChatRouteRequest(route));
        Assert.False(result.IsValid);
        var expected = $"Route must be one of: {string.Join(", ", OfficeRouteCatalog.KnownRoutes)}.";
        Assert.Contains(result.Errors, e => e.ErrorMessage == expected);
    }

    // ChatSendRequestValidator — valid prompt

    [Fact]
    public void ChatSendRequestValidator_NonEmptyPrompt_IsValid()
    {
        var result = _sendValidator.Validate(new ChatSendRequest("Hello!", null));
        Assert.True(result.IsValid);
    }

    // ChatSendRequestValidator — invalid: empty / whitespace prompt

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatSendRequestValidator_EmptyOrWhitespacePrompt_FailsWithRequiredMessage(string prompt)
    {
        var result = _sendValidator.Validate(new ChatSendRequest(prompt, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Prompt is required.");
    }
}

public class MLValidatorTests
{
    private readonly ResearchRunRequestValidator _researchValidator = new();
    private readonly WatchlistRunRequestValidator _watchlistValidator = new();

    // ResearchRunRequestValidator — valid query

    [Fact]
    public void ResearchRunRequestValidator_NonEmptyQuery_IsValid()
    {
        var result = _researchValidator.Validate(new ResearchRunRequest("AI trends", null, null));
        Assert.True(result.IsValid);
    }

    // ResearchRunRequestValidator — invalid: empty / whitespace query

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResearchRunRequestValidator_EmptyOrWhitespaceQuery_FailsWithRequiredMessage(string query)
    {
        var result = _researchValidator.Validate(new ResearchRunRequest(query, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Query is required.");
    }

    // WatchlistRunRequestValidator — valid watchlist ID

    [Fact]
    public void WatchlistRunRequestValidator_NonEmptyWatchlistId_IsValid()
    {
        var result = _watchlistValidator.Validate(new WatchlistRunRequest("wl-001", null));
        Assert.True(result.IsValid);
    }

    // WatchlistRunRequestValidator — invalid: empty / whitespace watchlist ID

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WatchlistRunRequestValidator_EmptyOrWhitespaceWatchlistId_FailsWithRequiredMessage(string watchlistId)
    {
        var result = _watchlistValidator.Validate(new WatchlistRunRequest(watchlistId, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "WatchlistId is required.");
    }
}

public class OperatorValidatorTests
{
    private readonly InboxResolveRequestValidator _validator = new();

    // InboxResolveRequestValidator — valid request

    [Theory]
    [InlineData("accepted")]
    [InlineData("deferred")]
    [InlineData("rejected")]
    public void InboxResolveRequestValidator_ValidStatus_IsValid(string status)
    {
        var result = _validator.Validate(new InboxResolveRequest("sg-001", status, null, null));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("ACCEPTED")]
    [InlineData("Deferred")]
    [InlineData("REJECTED")]
    public void InboxResolveRequestValidator_ValidStatusMixedCase_IsValid(string status)
    {
        var result = _validator.Validate(new InboxResolveRequest("sg-001", status, null, null));
        Assert.True(result.IsValid);
    }

    // InboxResolveRequestValidator — invalid: empty suggestion ID

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InboxResolveRequestValidator_EmptyOrWhitespaceSuggestionId_FailsWithRequiredMessage(string suggestionId)
    {
        var result = _validator.Validate(new InboxResolveRequest(suggestionId, "accepted", null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "SuggestionId is required.");
    }

    // InboxResolveRequestValidator — invalid: empty status

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InboxResolveRequestValidator_EmptyOrWhitespaceStatus_FailsWithRequiredMessage(string status)
    {
        var result = _validator.Validate(new InboxResolveRequest("sg-001", status, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Status is required.");
    }

    // InboxResolveRequestValidator — invalid: unknown status

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("pending")]
    public void InboxResolveRequestValidator_UnknownStatus_FailsWithKnownStatusesMessage(string status)
    {
        var result = _validator.Validate(new InboxResolveRequest("sg-001", status, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Status must be one of: accepted, deferred, rejected.");
    }
}

public class ScheduleValidatorTests
{
    private readonly CreateScheduleRequestValidator _createScheduleValidator = new();
    private readonly UpdateScheduleRequestValidator _updateScheduleValidator = new();
    private readonly CreateWorkflowRequestValidator _createWorkflowValidator = new();

    // CreateScheduleRequestValidator — valid requests

    [Theory]
    [InlineData("Daily report", "ml-analytics", "0 8 * * *")]
    [InlineData("Hourly sync", "watchlist-run", "every 30m")]
    [InlineData("Weekly digest", "knowledge-index", "every 2h")]
    public void CreateScheduleRequestValidator_ValidRequest_IsValid(string name, string jobType, string cron)
    {
        var result = _createScheduleValidator.Validate(new CreateScheduleRequest(name, jobType, cron, null, null));
        Assert.True(result.IsValid);
    }

    // CreateScheduleRequestValidator — invalid: empty name

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateScheduleRequestValidator_EmptyOrWhitespaceName_FailsWithRequiredMessage(string name)
    {
        var result = _createScheduleValidator.Validate(new CreateScheduleRequest(name, "ml-analytics", "0 8 * * *", null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Name is required.");
    }

    // CreateScheduleRequestValidator — invalid: empty job type

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateScheduleRequestValidator_EmptyOrWhitespaceJobType_FailsWithRequiredMessage(string jobType)
    {
        var result = _createScheduleValidator.Validate(new CreateScheduleRequest("Daily report", jobType, "0 8 * * *", null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "JobType is required.");
    }

    // CreateScheduleRequestValidator — invalid: empty cron expression

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateScheduleRequestValidator_EmptyOrWhitespaceCronExpression_FailsWithRequiredMessage(string cron)
    {
        var result = _createScheduleValidator.Validate(new CreateScheduleRequest("Daily report", "ml-analytics", cron, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "CronExpression is required.");
    }

    // CreateScheduleRequestValidator — invalid: malformed cron expression

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("every xm")]
    [InlineData("1 2 3")]
    public void CreateScheduleRequestValidator_InvalidCronExpression_FailsWithInvalidCronMessage(string cron)
    {
        var result = _createScheduleValidator.Validate(new CreateScheduleRequest("Daily report", "ml-analytics", cron, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("not a valid cron expression"));
    }

    // UpdateScheduleRequestValidator — null cron expression is valid (field is optional)

    [Fact]
    public void UpdateScheduleRequestValidator_NullCronExpression_IsValid()
    {
        var result = _updateScheduleValidator.Validate(new UpdateScheduleRequest(null, null, null, null));
        Assert.True(result.IsValid);
    }

    // UpdateScheduleRequestValidator — valid cron expression when provided

    [Theory]
    [InlineData("0 8 * * *")]
    [InlineData("every 30m")]
    public void UpdateScheduleRequestValidator_ValidCronExpression_IsValid(string cron)
    {
        var result = _updateScheduleValidator.Validate(new UpdateScheduleRequest(null, cron, null, null));
        Assert.True(result.IsValid);
    }

    // UpdateScheduleRequestValidator — invalid cron expression when provided

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("every xm")]
    public void UpdateScheduleRequestValidator_InvalidCronExpression_FailsWithInvalidCronMessage(string cron)
    {
        var result = _updateScheduleValidator.Validate(new UpdateScheduleRequest(null, cron, null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("not a valid cron expression"));
    }

    // CreateWorkflowRequestValidator — valid request

    [Fact]
    public void CreateWorkflowRequestValidator_ValidRequest_IsValid()
    {
        var steps = new List<CreateWorkflowStepRequest> { new("ml-analytics", null, null) };
        var result = _createWorkflowValidator.Validate(new CreateWorkflowRequest("My workflow", null, null, steps));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("continue")]
    [InlineData("ABORT")]
    [InlineData("Continue")]
    public void CreateWorkflowRequestValidator_ValidFailurePolicy_IsValid(string policy)
    {
        var steps = new List<CreateWorkflowStepRequest> { new("ml-analytics", null, null) };
        var result = _createWorkflowValidator.Validate(new CreateWorkflowRequest("My workflow", null, policy, steps));
        Assert.True(result.IsValid);
    }

    // CreateWorkflowRequestValidator — invalid: empty name

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateWorkflowRequestValidator_EmptyOrWhitespaceName_FailsWithRequiredMessage(string name)
    {
        var steps = new List<CreateWorkflowStepRequest> { new("ml-analytics", null, null) };
        var result = _createWorkflowValidator.Validate(new CreateWorkflowRequest(name, null, null, steps));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Name is required.");
    }

    // CreateWorkflowRequestValidator — invalid: no steps

    [Fact]
    public void CreateWorkflowRequestValidator_EmptySteps_FailsWithRequiredMessage()
    {
        var result = _createWorkflowValidator.Validate(
            new CreateWorkflowRequest("My workflow", null, null, new List<CreateWorkflowStepRequest>()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "At least one step is required.");
    }

    // CreateWorkflowRequestValidator — invalid: step missing JobType

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateWorkflowRequestValidator_StepWithEmptyJobType_FailsWithRequiredMessage(string jobType)
    {
        var steps = new List<CreateWorkflowStepRequest> { new(jobType, null, null) };
        var result = _createWorkflowValidator.Validate(new CreateWorkflowRequest("My workflow", null, null, steps));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "Step JobType is required.");
    }

    // CreateWorkflowRequestValidator — invalid: unknown failure policy

    [Theory]
    [InlineData("skip")]
    [InlineData("retry")]
    [InlineData("fail")]
    public void CreateWorkflowRequestValidator_InvalidFailurePolicy_FailsWithPolicyMessage(string policy)
    {
        var steps = new List<CreateWorkflowStepRequest> { new("ml-analytics", null, null) };
        var result = _createWorkflowValidator.Validate(new CreateWorkflowRequest("My workflow", null, policy, steps));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == "FailurePolicy must be 'abort' or 'continue'.");
    }
}
