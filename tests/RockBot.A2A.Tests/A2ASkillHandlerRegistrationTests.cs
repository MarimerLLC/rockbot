using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ASkillHandlerRegistrationTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        return services;
    }

    [TestMethod]
    public void AddSkillHandler_RegistersHandler_AsIAgentSkillHandler()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A()
            .AddSkillHandler<EchoSkillHandler>());

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider.GetServices<IAgentSkillHandler>().ToList();
        Assert.AreEqual(1, handlers.Count);
        Assert.AreEqual("echo", handlers[0].Skill.Id);
    }

    [TestMethod]
    public void AddSkillHandler_WiresUpDispatcher_AsIAgentTaskHandler()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A()
            .AddSkillHandler<EchoSkillHandler>());

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var taskHandler = scope.ServiceProvider.GetRequiredService<IAgentTaskHandler>();
        Assert.IsInstanceOfType<SkillDispatchingTaskHandler>(taskHandler);
    }

    [TestMethod]
    public void AddSkillHandlers_RegistersMultiple()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A()
            .AddSkillHandlers(typeof(EchoSkillHandler), typeof(SearchSkillHandler)));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider.GetServices<IAgentSkillHandler>().ToList();
        CollectionAssert.AreEquivalent(
            new[] { "echo", "search" },
            handlers.Select(h => h.Skill.Id).ToArray());
    }

    [TestMethod]
    public void AddSkillHandlers_NonSkillHandlerType_Throws()
    {
        var services = BaseServices();

        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddRockBotHost(agent => agent
                .WithIdentity("test-agent")
                .AddA2A()
                .AddSkillHandlers(typeof(string))));
    }

    [TestMethod]
    public async Task Validator_PopulatesAgentCardSkills_FromRegisteredHandlers()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A(opts =>
            {
                opts.Card = new AgentCard
                {
                    AgentName = "test-agent",
                    Version = "1.0"
                };
            })
            .AddSkillHandler<EchoSkillHandler>()
            .AddSkillHandler<SearchSkillHandler>());

        var provider = services.BuildServiceProvider();

        // Run only the validator, not the discovery service (which would try to publish).
        var validator = provider.GetRequiredService<SkillRegistrationValidator>();
        await validator.StartAsync(CancellationToken.None);

        var options = provider.GetRequiredService<A2AOptions>();
        var skillIds = options.Card!.Skills!.Select(s => s.Id).ToList();
        CollectionAssert.Contains(skillIds, "echo");
        CollectionAssert.Contains(skillIds, "search");
    }

    [TestMethod]
    public async Task Validator_MergesWithExistingCardSkills_WithoutDuplicates()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A(opts =>
            {
                opts.Card = new AgentCard
                {
                    AgentName = "test-agent",
                    Version = "1.0",
                    Skills =
                    [
                        new AgentSkill { Id = "echo", Name = "Pre-declared echo" },
                        new AgentSkill { Id = "preexisting", Name = "Preexisting" }
                    ]
                };
            })
            .AddSkillHandler<EchoSkillHandler>()
            .AddSkillHandler<SearchSkillHandler>());

        var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<SkillRegistrationValidator>();
        await validator.StartAsync(CancellationToken.None);

        var options = provider.GetRequiredService<A2AOptions>();
        var skills = options.Card!.Skills!.ToList();
        // echo should not be duplicated — the pre-declared entry wins.
        Assert.AreEqual(1, skills.Count(s => string.Equals(s.Id, "echo", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("Pre-declared echo", skills.First(s => s.Id == "echo").Name);
        Assert.IsTrue(skills.Any(s => s.Id == "preexisting"));
        Assert.IsTrue(skills.Any(s => s.Id == "search"));
    }

    [TestMethod]
    public async Task Validator_UserAlsoRegisteredIAgentTaskHandler_Throws()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent")
                 .AddA2A()
                 .AddSkillHandler<EchoSkillHandler>();
            // Ambiguous: user adds their own IAgentTaskHandler alongside skill handlers.
            agent.Services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();
        });

        var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<SkillRegistrationValidator>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Validator_NoSkillHandlers_Noop_ExistingFlowUnchanged()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent")
                 .AddA2A(opts => opts.Card = new AgentCard
                 {
                     AgentName = "test-agent",
                     Version = "1.0"
                 });
            agent.Services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();
        });

        var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<SkillRegistrationValidator>();

        await validator.StartAsync(CancellationToken.None);

        // The traditional IAgentTaskHandler registration should still resolve fine.
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IAgentTaskHandler>();
        Assert.IsInstanceOfType<StubAgentTaskHandler>(handler);
    }

    [TestMethod]
    public void Validator_IsRegisteredBeforeDiscoveryService()
    {
        var services = BaseServices();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A());
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        // The validator must start before discovery so it can populate the card
        // before it is announced. .NET generic host runs hosted services in
        // registration order, so checking the resolution order is sufficient.
        var hosted = provider.GetServices<IHostedService>().ToList();
        var validatorIndex = hosted.FindIndex(h => h is SkillRegistrationValidator);
        var discoveryIndex = hosted.FindIndex(h => h is AgentDiscoveryService);
        Assert.IsTrue(validatorIndex >= 0, "SkillRegistrationValidator should be registered");
        Assert.IsTrue(discoveryIndex >= 0, "AgentDiscoveryService should be registered");
        Assert.IsTrue(validatorIndex < discoveryIndex,
            $"Validator (at {validatorIndex}) must run before discovery (at {discoveryIndex})");
    }

    // ── Stubs ────────────────────────────────────────────────────────────

    private sealed class EchoSkillHandler : IAgentSkillHandler
    {
        public AgentSkill Skill { get; } = new() { Id = "echo", Name = "Echo" };
        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context) =>
            Task.FromResult(new AgentTaskResult
            {
                TaskId = request.TaskId,
                State = AgentTaskState.Completed
            });
    }

    private sealed class SearchSkillHandler : IAgentSkillHandler
    {
        public AgentSkill Skill { get; } = new() { Id = "search", Name = "Search" };
        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context) =>
            Task.FromResult(new AgentTaskResult
            {
                TaskId = request.TaskId,
                State = AgentTaskState.Completed
            });
    }

    private sealed class StubSubscriber : IMessageSubscriber
    {
        public Task<ISubscription> SubscribeAsync(
            string topic, string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ISubscription>(new StubSubscription());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSubscription : ISubscription
    {
        public string Topic => string.Empty;
        public string SubscriptionName => string.Empty;
        public bool IsActive => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
