namespace LinkForge;

public sealed record WorkflowNodeDefinition(
    string Id,
    string Stage,
    string[] DependsOn,
    string EntryGate,
    string ExitGate,
    int RetryLimit = 0,
    bool ApprovalRequired = false,
    bool HighImpact = false);

public sealed record ScenarioDefinition(
    string Id,
    string Title,
    string Requirements,
    string[] Assumptions,
    string[] AcceptanceCriteria,
    WorkflowNodeDefinition[] Nodes);

public static class ScenarioLibrary
{
    public const string RequirementsNode = "requirements";
    public const string ImplementationNode = "implementation";
    public const string TestsNode = "tests";
    public const string ChangeApprovalNode = "change-approval";
    public const string ValidationNode = "validation";

    /// <summary>
    /// requirements -> architecture -> {implementation || tests} -> change-approval (human) ->
    /// {validation (build/test/security) || documentation} -> review -> release-approval (human) -> release-readiness.
    /// </summary>
    private static WorkflowNodeDefinition[] Graph() =>
    [
        new(RequirementsNode, "requirements", [], "Requirement exists and scope is understood.",
            "Normalized problem, assumptions, ambiguities and acceptance criteria are recorded."),
        new("architecture", "architecture", [RequirementsNode], "Requirements passed their exit gate.",
            "Impacted modules, component boundaries and design decisions are documented."),
        new(ImplementationNode, "implementation", ["architecture"], "Architecture decisions are recorded.",
            "A bounded, policy-compliant change set is proposed and remains unapplied.", RetryLimit: 1),
        new(TestsNode, "testing", ["architecture"], "Architecture decisions are recorded.",
            "Concrete test checks (and optional test code) are proposed within change policy.", RetryLimit: 1),
        new(ChangeApprovalNode, "governance", [ImplementationNode, TestsNode],
            "Implementation and test proposals both passed policy checks.",
            "An authorized human approves the exact change-set digest, or rejects it.",
            ApprovalRequired: true, HighImpact: true),
        new(ValidationNode, "validation", [ChangeApprovalNode],
            "Change set was approved by a human for sandbox validation.",
            "Policy, security scan, build and tests all pass on an isolated copy.", RetryLimit: 1),
        new("documentation", "documentation", [ChangeApprovalNode], "Change set was approved by a human.",
            "Setup, API and operating guidance are recorded."),
        new("review", "quality", [ValidationNode, "documentation"],
            "Validation and documentation branches have both completed.",
            "Risks, policy results and a release recommendation are recorded."),
        new("release-approval", "release", ["review"], "Review passed and release scope is bounded.",
            "An authorized human explicitly approves release readiness.",
            ApprovalRequired: true, HighImpact: true),
        new("release-readiness", "release", ["release-approval"], "Release approval is recorded.",
            "Release checklist is complete; deployment remains out of scope.")
    ];

    private static readonly ScenarioDefinition[] Definitions =
    [
        new("greenfield", "Greenfield: link expiry for the URL shortener",
            "Add support for optional link expiry to the shortener: a link created with an expiresAt timestamp stops resolving after that time, while existing links without expiry keep working unchanged. Use HTTP(S) destinations only.",
            ["Data is stored in SQLite through EF Core.",
             "Authentication, abuse prevention and production deployment are out of scope for this change."],
            ["Expired links return 404 and are not counted as clicks.",
             "Links without expiry behave exactly as before.",
             "Expiry timestamps in the past are rejected at creation.",
             "Automated xUnit tests cover the new behavior."],
            Graph()),
        new("brownfield", "Brownfield: harden click analytics",
            "Review the existing click-analytics implementation and propose a compatibility-preserving improvement for referrer reporting (for example, a bounded top-referrers summary) without changing existing short-link behavior.",
            ["Existing short codes, destinations and click counters remain stable.",
             "Migrating off SQLite is not part of this change."],
            ["Existing short links continue to resolve and count clicks.",
             "Concurrent resolutions do not lose recorded clicks.",
             "The change is additive and regression-tested."],
            Graph()),
        new("ambiguous", "Ambiguous: define a safe sharing experience",
            "Make links easier to share and understand, while keeping users safe.",
            ["'Easier to share' means readable aliases and a copyable short URL, not social integrations.",
             "'Safe' means strict HTTP(S) validation and no destination fetching in this release.",
             "Abuse reporting, authentication, privacy retention and link expiration need product decisions."],
            ["Present open questions separately from accepted assumptions.",
             "Validate destination scheme without fetching user-provided URLs.",
             "Identify security and privacy trade-offs for human review.",
             "Block release until a human reviews scope and risk."],
            Graph())
    ];

    public static IReadOnlyList<ScenarioDefinition> All => Definitions;

    public static ScenarioDefinition? Find(string? scenarioId) =>
        Definitions.SingleOrDefault(scenario =>
            string.Equals(scenario.Id, scenarioId, StringComparison.OrdinalIgnoreCase));
}
