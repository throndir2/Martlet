using System.Collections.Immutable;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class InstallationPlannerTests
{
    private static readonly Guid Client = Id(1);
    private static readonly Guid Host = Id(2);
    private static readonly InstallationFact Eligible = new(PlanningState.Eligible, "supplied-planning-fact");
    private static readonly InstallationOwner Managed = new(InstallationOwnership.GuidedManaged, Id(10));
    private static readonly InstallationOwner External = new(InstallationOwnership.ExistingService, Id(11));
    private static readonly InstallationResources Capacity = new(16000, 32000, 16000, 200000);
    private static readonly InstallationResources Small = new(1000, 1000, 1000, 1000);

    [Fact]
    public void TypedOnlyProjectsLlmAndLeavesSavedSpeechCompletelyInert()
    {
        var request = Full() with { Features = [InstallationFeature.TypedConversation] };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Equal(InstallationRole.Llm, Assert.Single(plan.Roles).Role);
        var api = Assert.Single(plan.Machines);
        Assert.Null(api.HostId);
        Assert.Empty(api.Resources);
        Assert.Equal(new[] { InstallationPrerequisite.CredentialBinding, InstallationPrerequisite.DataConsent },
            api.Prerequisites.Select(p => p.Kind));
        Assert.All(api.Operations, op => Assert.Equal(InstallationRole.Llm, Assert.Single(op.Roles)));
        Assert.Equal(5, request.Destinations.Length);
        Assert.Equal(3, request.Runtimes.Length);
    }

    [Fact]
    public void FixtureAndEmptySelectionRequireNoRuntimeKeysDevicesOrHostChecks()
    {
        foreach (var features in new[] { ImmutableArray<InstallationFeature>.Empty, [InstallationFeature.Fixture] })
        {
            var plan = InstallationPlanner.Create(Full() with { Features = features });
            Assert.Equal(PlanningState.Eligible, plan.State);
            Assert.Empty(plan.Roles);
            Assert.Empty(plan.Machines);
        }
    }

    [Fact]
    public void MicrophoneRequiresCaptureAndSttButNeverTtsOrOutput()
    {
        var plan = InstallationPlanner.Create(Full() with { Features = [InstallationFeature.MicrophoneInput] });
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Equal(new[] { InstallationRole.Llm, InstallationRole.Stt, InstallationRole.Capture }, plan.Roles.Select(r => r.Role));
        Assert.DoesNotContain(plan.Machines.SelectMany(m => m.Prerequisites), p => p.Kind == InstallationPrerequisite.OutputDevice);
    }

    [Fact]
    public void SpokenTypedRepliesRequireTtsAndOutputButNeverSttOrCapture()
    {
        var plan = InstallationPlanner.Create(Full() with { Features = [InstallationFeature.SpokenReplies] });
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Equal(new[] { InstallationRole.Llm, InstallationRole.Tts, InstallationRole.Output }, plan.Roles.Select(r => r.Role));
        Assert.DoesNotContain(plan.Machines.SelectMany(m => m.Prerequisites), p => p.Kind == InstallationPrerequisite.CaptureDevice);
    }

    [Fact]
    public void HybridGroupsApiClientAndManagedHostWithoutRequiringCloudSpeechKeys()
    {
        var plan = InstallationPlanner.Create(Full());
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Equal(3, plan.Machines.Length);
        var host = plan.Machines.Single(m => m.HostId == Host);
        Assert.Equal(new[] { InstallationRole.Stt, InstallationRole.Tts }, Assert.Single(host.Runtimes).Roles);
        Assert.Single(host.Resources);
        Assert.Equal(Small, host.Demand);
        Assert.Single(host.Operations.Where(o => o.Kind == InstallationReviewOperation.ReviewManagedRuntime));
        Assert.Single(host.Operations.Where(o => o.Kind == InstallationReviewOperation.ReviewManagedResource));
        Assert.Contains(host.Prerequisites, p => p.Kind == InstallationPrerequisite.Pairing);
        Assert.DoesNotContain(host.Prerequisites, p => p.Kind == InstallationPrerequisite.CredentialBinding);
        Assert.DoesNotContain(plan.Machines.Single(m => m.HostId == Client).Prerequisites,
            p => p.Kind == InstallationPrerequisite.Pairing);
    }

    [Fact]
    public void CoLocatedProviderRolesShareOneInstanceAndOneDependencyBudget()
    {
        var request = Full();
        request = request with
        {
            Destinations = request.Destinations.Select(d => d.Role == InstallationRole.Llm
                ? Destination(InstallationRole.Llm, Id(21)) : d).ToImmutableArray()
        };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Equal(2, plan.Machines.Length);
        var host = plan.Machines.Single(m => m.HostId == Host);
        Assert.Equal(3, Assert.Single(host.Runtimes).Roles.Length);
        Assert.Equal(Small, host.Demand);
        Assert.Single(host.Prerequisites.Where(p => p.Kind == InstallationPrerequisite.Pairing));
        Assert.DoesNotContain(plan.Machines.SelectMany(m => m.Prerequisites), p => p.Kind == InstallationPrerequisite.CredentialBinding);
    }

    [Theory]
    [InlineData(InstallationOwnership.ExistingService)]
    [InlineData(InstallationOwnership.UserManagedCompose)]
    public void ExternalOwnershipNeverProducesManagedOperations(InstallationOwnership mode)
    {
        var request = Full();
        var owner = new InstallationOwner(mode, Id(100));
        request = request with
        {
            Runtimes = request.Runtimes.Select(r => r.Id == Id(21) ? r with { Owner = owner } : r).ToImmutableArray(),
            Resources = request.Resources.Select(r => r.HostId == Host ? r with { Owner = owner } : r).ToImmutableArray()
        };
        var host = InstallationPlanner.Create(request).Machines.Single(m => m.HostId == Host);
        Assert.Contains(host.Operations, o => o.Kind == InstallationReviewOperation.ReviewExternalConnection);
        Assert.DoesNotContain(host.Operations, o => o.Kind is
            InstallationReviewOperation.ReviewManagedResource or InstallationReviewOperation.ReviewManagedRuntime);
    }

    [Fact]
    public void ExternalDependencyCanBeReusedButNotAdopted()
    {
        var request = Full();
        request = request with { Resources = request.Resources.SetItem(0, request.Resources[0] with { Owner = External }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        var host = plan.Machines.Single(m => m.HostId == Host);
        Assert.Equal(External, Assert.Single(host.Resources).Owner);
        Assert.DoesNotContain(host.Operations, o => o.Kind == InstallationReviewOperation.ReviewManagedResource);
    }

    [Fact]
    public void ManagedResourcesOfAnotherOwnerCannotBeTakenOver()
    {
        var request = Full();
        request = request with { Resources = request.Resources.SetItem(0, request.Resources[0] with
        {
            Owner = Managed with { Id = Id(999) }
        }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Blocked, plan.State);
        Assert.Contains(plan.Roles.Single(r => r.Role == InstallationRole.Tts).Issues,
            i => i.Reason == PlanningReason.OwnershipConflict);
        Assert.Empty(plan.Machines.Single(m => m.HostId == Host).Operations);
        Assert.Equal(PlanningState.Eligible, plan.Roles.Single(r => r.Role == InstallationRole.Llm).State);
    }

    [Fact]
    public void SharedTransitiveDiamondDependencyIsCountedAndReviewedOnce()
    {
        var request = Full();
        var a = Resource(40, "runtime-a", Small, [Id(42), Id(43)]);
        var b = Resource(41, "runtime-b", Small, [Id(43)]);
        var c = Resource(42, "engine", Small, [Id(43)]);
        var d = Resource(43, "shared-model", Small, []);
        var second = request.Runtimes[1] with { Id = Id(24), ResourceIds = [b.Id] };
        request = request with
        {
            Resources = [a, b, c, d, request.Resources[1]],
            Runtimes = request.Runtimes.SetItem(1, request.Runtimes[1] with { ResourceIds = [a.Id] }).Add(second),
            Destinations = request.Destinations.Select(r => r.Role == InstallationRole.Tts ? r with { RuntimeId = second.Id } : r).ToImmutableArray()
        };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        var host = plan.Machines.Single(m => m.HostId == Host);
        Assert.Equal(new InstallationResources(4000, 4000, 4000, 4000), host.Demand);
        Assert.Equal(4, host.Resources.Length);
        Assert.Single(host.Operations.Where(o => o.SubjectId == Id(43)));
        Assert.Equal(new[] { InstallationRole.Stt, InstallationRole.Tts }, host.Resources.Single(r => r.Id == Id(43)).Roles);
    }

    [Fact]
    public void InUseExternalReservationsConsumePhysicalCapacityWithoutGrantingOperations()
    {
        var request = Full();
        var occupied = Resource(50, "other-application", new(0, 32000, 0, 0), []) with { InUse = true, Owner = External };
        request = request with { Resources = request.Resources.Add(occupied) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Blocked, plan.State);
        var host = plan.Machines.Single(m => m.HostId == Host);
        Assert.Equal(33000, host.Demand.RamMiB);
        Assert.Empty(host.Resources.Single(r => r.Id == occupied.Id).Roles);
        Assert.Empty(host.Operations);
        Assert.Equal(PlanningState.Eligible, plan.Roles.Single(r => r.Role == InstallationRole.Llm).State);
    }

    [Fact]
    public void InactiveUnoccupiedResourcesDoNotConsumeCapacityOrCauseConflicts()
    {
        var request = Full();
        request = request with { Resources = request.Resources.Add(Resource(50, "worker", Capacity, [])) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Single(plan.Machines.Single(m => m.HostId == Host).Resources);
    }

    [Fact]
    public void OccupiedSlotCollisionIsExplicitEvenWhenResourceDemandsAreZero()
    {
        var request = Full();
        request = request with { Resources = request.Resources.Add(Resource(50, "worker", InstallationResources.Zero, []) with { InUse = true }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Contains(plan.Roles.Single(r => r.Role == InstallationRole.Stt).Issues, i => i.Reason == PlanningReason.ResourceConflict);
        Assert.Empty(plan.Machines.Single(m => m.HostId == Host).Operations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachCapacityDimensionHasExactBoundaryAndUnknownState(int dimension)
    {
        InstallationResources Set(long? value) => dimension switch
        {
            0 => Capacity with { CpuMilliCores = value },
            1 => Capacity with { RamMiB = value },
            2 => Capacity with { VramMiB = value },
            _ => Capacity with { DiskMiB = value }
        };
        foreach (var (value, state) in new (long?, PlanningState)[]
        {
            (1000, PlanningState.Eligible), (999, PlanningState.Blocked), (null, PlanningState.Unknown)
        })
        {
            var request = Full();
            request = request with { Hosts = request.Hosts.SetItem(1, request.Hosts[1] with { Capacity = Set(value) }) };
            var plan = InstallationPlanner.Create(request);
            Assert.Equal(state, plan.Roles.Single(r => r.Role == InstallationRole.Tts).State);
            if (state != PlanningState.Eligible) Assert.Empty(plan.Machines.Single(m => m.HostId == Host).Operations);
        }
    }

    [Fact]
    public void UnknownDemandStaysUnknownButZeroDemandNeedsNoCapacityEvidence()
    {
        var request = Full();
        request = request with { Resources = request.Resources.SetItem(0, request.Resources[0] with { Demand = Small with { RamMiB = null } }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Unknown, plan.State);
        Assert.Null(plan.Machines.Single(m => m.HostId == Host).Demand.RamMiB);
        request = request with
        {
            Resources = request.Resources.SetItem(0, request.Resources[0] with { Demand = InstallationResources.Zero }),
            Hosts = request.Hosts.SetItem(1, request.Hosts[1] with { Capacity = new(null, null, null, null) })
        };
        Assert.Equal(PlanningState.Eligible, InstallationPlanner.Create(request).State);
    }

    [Theory]
    [InlineData(CapabilitySupport.Unknown, PlanningState.Unknown, PlanningReason.CapabilityUnknown)]
    [InlineData(CapabilitySupport.Unsupported, PlanningState.Unavailable, PlanningReason.CapabilityUnsupported)]
    public void CapabilityNeverFallsBackOrInfersSupport(CapabilitySupport capability, PlanningState state, PlanningReason reason)
    {
        var request = Full() with { Features = [InstallationFeature.TypedConversation] };
        request = request with { Destinations = request.Destinations.SetItem(0, request.Destinations[0] with { Capability = capability }) };
        var plan = InstallationPlanner.Create(request);
        var role = Assert.Single(plan.Roles);
        Assert.Equal(state, role.State);
        Assert.Equal(Id(20), role.RuntimeId);
        Assert.Contains(role.Issues, i => i.Reason == reason);
        Assert.Empty(Assert.Single(plan.Machines).Operations);
    }

    [Theory]
    [InlineData(PlanningState.Unknown)]
    [InlineData(PlanningState.Blocked)]
    [InlineData(PlanningState.Unavailable)]
    public void RuntimeDestinationAndResourceEligibilityAreIndependentGates(PlanningState state)
    {
        var fact = new InstallationFact(state, "exact-tuple-not-qualified");
        var baseline = Full();
        foreach (var request in new[]
        {
            baseline with { Runtimes = baseline.Runtimes.SetItem(1, baseline.Runtimes[1] with { Eligibility = fact }) },
            baseline with { Resources = baseline.Resources.SetItem(0, baseline.Resources[0] with { Eligibility = fact }) },
            baseline with { Destinations = baseline.Destinations.SetItem(2, baseline.Destinations[2] with { Eligibility = fact }) }
        })
        {
            var plan = InstallationPlanner.Create(request);
            Assert.Equal(state, plan.Roles.Single(r => r.Role == InstallationRole.Tts).State);
            Assert.DoesNotContain(plan.Machines.SelectMany(m => m.Operations), o => o.Roles.Contains(InstallationRole.Tts));
            Assert.Equal(PlanningState.Eligible, plan.Roles.Single(r => r.Role == InstallationRole.Llm).State);
        }
    }

    [Fact]
    public void MissingMandatoryFactsAreUnknownNotInventedPasses()
    {
        var request = Full();
        request = request with
        {
            Runtimes = request.Runtimes.Select(r => r with { Prerequisites = [] }).ToImmutableArray(),
            Destinations = request.Destinations.Select(d => d with { Prerequisites = [] }).ToImmutableArray()
        };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Unknown, plan.State);
        Assert.All(plan.Roles, r => Assert.Equal(PlanningState.Unknown, r.State));
        Assert.All(plan.Machines.SelectMany(m => m.Prerequisites), p => Assert.Equal(InstallationFact.Unknown, p.Fact));
        Assert.All(plan.Machines, m => Assert.Empty(m.Operations));
    }

    [Fact]
    public void MissingDestinationBlocksOnlyDependentExperienceAndNeverUsesAnotherRole()
    {
        var request = Full() with { Features = [InstallationFeature.TypedConversation, InstallationFeature.SpokenReplies] };
        request = request with { Destinations = request.Destinations.Where(d => d.Role != InstallationRole.Tts).ToImmutableArray() };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.Features.Single(f => f.Feature == InstallationFeature.TypedConversation).State);
        Assert.Equal(PlanningState.Blocked, plan.Features.Single(f => f.Feature == InstallationFeature.SpokenReplies).State);
        var missing = plan.Roles.Single(r => r.Role == InstallationRole.Tts);
        Assert.Null(missing.RuntimeId);
        Assert.Equal(PlanningReason.DestinationMissing, Assert.Single(missing.Issues).Reason);
    }

    [Fact]
    public void FutureProductsRemainUnavailableEvenWithExplicitSupportedFacts()
    {
        var request = Full() with
        {
            Features = [InstallationFeature.TypedConversation, InstallationFeature.Perception, InstallationFeature.Memory, InstallationFeature.Avatar]
        };
        request = request with { Destinations = request.Destinations.AddRange(new[] {
            Destination(InstallationRole.ScreenCapture, Id(22)), Destination(InstallationRole.Perception, Id(21)),
            Destination(InstallationRole.Memory, Id(21)), Destination(InstallationRole.Avatar, Id(22))
        }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Unavailable, plan.State);
        Assert.Equal(PlanningState.Eligible, plan.Features.Single(f => f.Feature == InstallationFeature.TypedConversation).State);
        Assert.All(plan.Roles.Where(r => r.Role != InstallationRole.Llm), r =>
        {
            Assert.Equal(PlanningState.Unavailable, r.State);
            Assert.Null(r.RuntimeId);
            Assert.Equal(PlanningReason.FutureFeature, Assert.Single(r.Issues).Reason);
        });
        Assert.Null(Assert.Single(plan.Machines).HostId);
    }

    [Fact]
    public void AllFeatureSubsetsProduceOnlySelectedImplementedRoleOperations()
    {
        var values = Enum.GetValues<InstallationFeature>();
        for (var mask = 0; mask < 1 << values.Length; mask++)
        {
            var features = values.Where((_, index) => (mask & (1 << index)) != 0).ToImmutableArray();
            var plan = InstallationPlanner.Create(Full() with { Features = features });
            var roles = plan.Features.SelectMany(f => f.RequiredRoles).Distinct().Order().ToArray();
            Assert.Equal(roles, plan.Roles.Select(r => r.Role));
            foreach (var op in plan.Machines.SelectMany(m => m.Operations))
                Assert.All(op.Roles, role => Assert.Equal(PlanningState.Eligible, plan.Roles.Single(r => r.Role == role).State));
            Assert.True(plan.Roles.Length <= 9);
            Assert.True(plan.Machines.Length <= InstallationRequest.MaxHosts + 1);
        }
    }

    [Fact]
    public void InputPermutationAndRepeatedPlanningProduceIdenticalSerializedOutput()
    {
        var request = Full();
        var expected = JsonSerializer.Serialize(InstallationPlanner.Create(request));
        request = request with
        {
            Features = request.Features.Reverse().ToImmutableArray(),
            Hosts = request.Hosts.Reverse().ToImmutableArray(),
            Resources = request.Resources.Reverse().ToImmutableArray(),
            Runtimes = request.Runtimes.Reverse().Select(r => r with
            {
                ResourceIds = r.ResourceIds.Reverse().ToImmutableArray(),
                Prerequisites = r.Prerequisites.Reverse().ToImmutableArray()
            }).ToImmutableArray(),
            Destinations = request.Destinations.Reverse().Select(d => d with
            {
                Prerequisites = d.Prerequisites.Reverse().ToImmutableArray()
            }).ToImmutableArray()
        };
        for (var i = 0; i < 5; i++) Assert.Equal(expected, JsonSerializer.Serialize(InstallationPlanner.Create(request)));
    }

    [Fact]
    public void OutputDoesNotChangeWhenCallerBuildsNewSelections()
    {
        var request = Full();
        var plan = InstallationPlanner.Create(request);
        var before = JsonSerializer.Serialize(plan);
        var builder = request.Destinations.ToBuilder();
        builder.Clear();
        request = request with { Destinations = builder.ToImmutable(), Features = [] };
        Assert.Empty(InstallationPlanner.Create(request).Roles);
        Assert.Equal(before, JsonSerializer.Serialize(plan));
    }

    [Fact]
    public void DisabledBlockedChoicesDoNotCreateDemandsPrerequisitesOrOperations()
    {
        var request = Full() with { Features = [InstallationFeature.TypedConversation] };
        request = request with
        {
            Runtimes = request.Runtimes.SetItem(1, request.Runtimes[1] with
            {
                Eligibility = new(PlanningState.Unavailable, "future-engine"),
                Prerequisites = [new(InstallationPrerequisite.Reboot, new(PlanningState.Blocked, "reboot-pending"))]
            }),
            Resources = request.Resources.SetItem(0, request.Resources[0] with { Demand = new(null, null, null, null) })
        };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        Assert.Null(Assert.Single(plan.Machines).HostId);
        Assert.DoesNotContain(plan.Machines.SelectMany(m => m.Prerequisites), p => p.Kind == InstallationPrerequisite.Reboot);
    }

    [Fact]
    public void AdditionalRebootGateBlocksOnlyItsHostAndHasAnExplicitReason()
    {
        var request = Full();
        request = request with { Runtimes = request.Runtimes.SetItem(1, request.Runtimes[1] with
        {
            Prerequisites = HostFacts.Add(new(InstallationPrerequisite.Reboot, new(PlanningState.Blocked, "reboot-pending")))
        }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Blocked, plan.State);
        Assert.Equal(PlanningState.Eligible, plan.Features.Single(f => f.Feature == InstallationFeature.TypedConversation).State);
        Assert.Contains(plan.Roles.Single(r => r.Role == InstallationRole.Tts).Issues,
            i => i.DetailId == "reboot-pending" && i.Reason == PlanningReason.Prerequisite);
        Assert.Empty(plan.Machines.Single(m => m.HostId == Host).Operations);
    }

    [Fact]
    public void OneBlockedRoleCannotAppearInSharedRuntimeOrResourceReviewOperations()
    {
        var request = Full();
        request = request with { Destinations = request.Destinations.SetItem(2, request.Destinations[2] with
        {
            Eligibility = new(PlanningState.Unavailable, "tts-adapter-not-implemented")
        }) };
        var host = InstallationPlanner.Create(request).Machines.Single(m => m.HostId == Host);
        Assert.All(host.Operations, o => Assert.Equal(InstallationRole.Stt, Assert.Single(o.Roles)));
        Assert.Equal(3, host.Operations.Length);
    }

    [Fact]
    public void ClientCanHostInferenceAlongsideAudioWithoutAnotherPhysicalBudget()
    {
        var request = Full();
        request = request with
        {
            Runtimes = request.Runtimes.SetItem(1, request.Runtimes[1] with
            {
                HostId = Client, Prerequisites = HostFacts.Where(p => p.Kind != InstallationPrerequisite.Pairing).ToImmutableArray()
            }),
            Resources = request.Resources.SetItem(0, request.Resources[0] with { HostId = Client }),
            Destinations = request.Destinations.SetItem(0, Destination(InstallationRole.Llm, Id(21)))
        };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(PlanningState.Eligible, plan.State);
        var machine = Assert.Single(plan.Machines);
        Assert.Equal(Client, machine.HostId);
        Assert.Equal(new InstallationResources(1100, 1100, 1000, 1000), machine.Demand);
        Assert.Equal(2, machine.Runtimes.Length);
        Assert.DoesNotContain(machine.Prerequisites, p => p.Kind == InstallationPrerequisite.Pairing);
    }

    [Fact]
    public void StableHostIdentitiesNotLabelsControlGrouping()
    {
        var request = Full();
        request = request with { Hosts = request.Hosts.SetItem(1, request.Hosts[1] with { Label = request.Hosts[0].Label }) };
        var plan = InstallationPlanner.Create(request);
        Assert.Equal(3, plan.Machines.Length);
        Assert.Equal(new Guid?[] { null, Client, Host }, plan.Machines.Select(m => m.HostId));
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        var r = Full();
        yield return [null!];
        yield return [r with { ClientHostId = Guid.Empty }];
        yield return [r with { ClientHostId = Id(999) }];
        yield return [r with { Features = default }];
        yield return [r with { Features = [(InstallationFeature)99] }];
        yield return [r with { Features = [InstallationFeature.Fixture, InstallationFeature.Fixture] }];
        yield return [r with { Hosts = default }];
        yield return [r with { Hosts = r.Hosts.Add(r.Hosts[0]) }];
        yield return [r with { Hosts = [null!] }];
        yield return [r with { Hosts = r.Hosts.SetItem(0, r.Hosts[0] with { Label = "\0" }) }];
        yield return [r with { Hosts = r.Hosts.SetItem(0, r.Hosts[0] with { Capacity = null! }) }];
        yield return [r with { Hosts = r.Hosts.SetItem(0, r.Hosts[0] with { Capacity = Capacity with { RamMiB = -1 } }) }];
        yield return [r with { Hosts = r.Hosts.SetItem(0, r.Hosts[0] with { Capacity = Capacity with { DiskMiB = InstallationRequest.MaxResourceQuantity + 1 } }) }];
        yield return [r with { Runtimes = default }];
        yield return [r with { Runtimes = r.Runtimes.Add(r.Runtimes[0]) }];
        yield return [r with { Runtimes = [null!] }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(0, r.Runtimes[0] with { Kind = (InstallationRuntimeKind)99 }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(0, r.Runtimes[0] with { Owner = Managed }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(0, r.Runtimes[0] with { HostId = Client }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(0, r.Runtimes[0] with { ResourceIds = [Id(30)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(0, r.Runtimes[0] with { EngineId = "https://secret@example.test" }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Owner = null! }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Owner = Managed with { Mode = (InstallationOwnership)99 } }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Owner = Managed with { Id = Guid.Empty } }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Eligibility = null! }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Eligibility = new((PlanningState)99, "bad") }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { HostId = Id(999) }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { ResourceIds = [] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { ResourceIds = [Id(999)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { ResourceIds = [Id(31)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { ResourceIds = [Id(30), Id(30)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = default }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = [null!] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = [new((InstallationPrerequisite)99, Eligible)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = [new(InstallationPrerequisite.Pairing, null!)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = [new(InstallationPrerequisite.Pairing, Eligible), new(InstallationPrerequisite.Pairing, Eligible)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = [new(InstallationPrerequisite.CredentialBinding, Eligible)] }) }];
        yield return [r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Eligibility = Eligible with { ReasonId = "" } }) }];
        yield return [r with { Resources = default }];
        yield return [r with { Resources = [null!] }];
        yield return [r with { Resources = r.Resources.Add(r.Resources[0]) }];
        yield return [r with { Resources = r.Resources.SetItem(0, r.Resources[0] with { Dependencies = [Id(30)] }) }];
        yield return [r with { Resources = r.Resources.SetItem(0, r.Resources[0] with { Dependencies = [Id(31)] }) }];
        yield return [r with { Resources = r.Resources.SetItem(0, r.Resources[0] with { Dependencies = [Id(999)] }) }];
        yield return [r with { Resources = r.Resources.SetItem(0, r.Resources[0] with { Dependencies = default }) }];
        yield return [r with { Destinations = default }];
        yield return [r with { Destinations = [null!] }];
        yield return [r with { Destinations = r.Destinations.Add(r.Destinations[0]) }];
        yield return [r with { Destinations = r.Destinations.SetItem(0, r.Destinations[0] with { Role = (InstallationRole)99 }) }];
        yield return [r with { Destinations = r.Destinations.SetItem(0, r.Destinations[0] with { RuntimeId = Id(999) }) }];
        yield return [r with { Destinations = r.Destinations.SetItem(0, r.Destinations[0] with { Capability = (CapabilitySupport)99 }) }];
        yield return [r with { Destinations = r.Destinations.SetItem(0, r.Destinations[0] with { Prerequisites = [new(InstallationPrerequisite.Pairing, Eligible)] }) }];
        yield return [r with { Destinations = r.Destinations.SetItem(0, r.Destinations[0] with { Prerequisites = [new(InstallationPrerequisite.OutputDevice, Eligible)] }) }];
        yield return [r with { Destinations = r.Destinations.SetItem(3, r.Destinations[3] with { RuntimeId = Id(21) }) }];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void MalformedIncludingInactiveConfigurationFailsExplicitly(InstallationRequest request) =>
        Assert.Equal(ErrorCode.InvalidContract, Assert.Throws<ContractException>(() => InstallationPlanner.Create(request)).Code);

    [Fact]
    public void AllCollectionCapsRejectBeforeTraversal()
    {
        var r = Full();
        foreach (var invalid in new[]
        {
            r with { Features = Enumerable.Repeat(InstallationFeature.Fixture, 8).ToImmutableArray() },
            r with { Hosts = Enumerable.Repeat(r.Hosts[0], InstallationRequest.MaxHosts + 1).ToImmutableArray() },
            r with { Runtimes = Enumerable.Repeat(r.Runtimes[0], InstallationRequest.MaxRuntimes + 1).ToImmutableArray() },
            r with { Resources = Enumerable.Repeat(r.Resources[0], InstallationRequest.MaxResources + 1).ToImmutableArray() },
            r with { Destinations = Enumerable.Repeat(r.Destinations[0], 10).ToImmutableArray() },
            r with { Resources = r.Resources.SetItem(0, r.Resources[0] with { Dependencies = Enumerable.Repeat(Id(30), 17).ToImmutableArray() }) },
            r with { Runtimes = r.Runtimes.SetItem(1, r.Runtimes[1] with { Prerequisites = Enumerable.Repeat(new PrerequisiteFact(InstallationPrerequisite.Pairing, Eligible), 12).ToImmutableArray() }) }
        })
            Assert.Throws<ContractException>(() => InstallationPlanner.Create(invalid));
    }

    [Fact]
    public void LongDependencyCycleFailsExplicitly()
    {
        var r = Full();
        var resources = Enumerable.Range(100, 100).Select(n =>
            Resource(n, $"slot-{n}", InstallationResources.Zero, [Id(n == 199 ? 100 : n + 1)])).ToImmutableArray();
        Assert.Throws<ContractException>(() => InstallationPlanner.Create(r with { Resources = r.Resources.AddRange(resources) }));
    }

    [Fact]
    public void MaximumResourceSumIsBoundedWithoutOverflow()
    {
        var r = Full();
        var resources = Enumerable.Range(100, 126).Select(n =>
            Resource(n, $"slot-{n}", new(InstallationRequest.MaxResourceQuantity, 0, 0, 0), []) with { InUse = true }).ToImmutableArray();
        var plan = InstallationPlanner.Create(r with { Resources = r.Resources.AddRange(resources) });
        Assert.Equal(126 * InstallationRequest.MaxResourceQuantity + 1000, plan.Machines.Single(m => m.HostId == Host).Demand.CpuMilliCores);
        Assert.Equal(PlanningState.Blocked, plan.State);
    }

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static ImmutableArray<PrerequisiteFact> HostFacts =>
        new[] { InstallationPrerequisite.HostQualification, InstallationPrerequisite.RuntimeCompatibility,
            InstallationPrerequisite.ArtifactEligibility, InstallationPrerequisite.LicenseReview,
            InstallationPrerequisite.LocalApproval, InstallationPrerequisite.Pairing }
        .Select(k => new PrerequisiteFact(k, Eligible)).ToImmutableArray();

    private static InstallationResource Resource(int id, string slot, InstallationResources demand, ImmutableArray<Guid> dependencies) =>
        new(Id(id), Host, slot, Managed, demand, false, Eligible, dependencies);

    private static InstallationDestination Destination(InstallationRole role, Guid runtimeId)
    {
        var facts = role switch
        {
            InstallationRole.Capture or InstallationRole.ScreenCapture =>
                new[] { InstallationPrerequisite.CaptureDevice, InstallationPrerequisite.DataConsent },
            InstallationRole.Output => [InstallationPrerequisite.OutputDevice],
            _ => [InstallationPrerequisite.DataConsent]
        };
        if (runtimeId == Id(20)) facts = [InstallationPrerequisite.CredentialBinding, InstallationPrerequisite.DataConsent];
        return new(role, runtimeId, $"selection-{role}", CapabilitySupport.Supported, Eligible,
            facts.Select(k => new PrerequisiteFact(k, Eligible)).ToImmutableArray());
    }

    private static InstallationRequest Full() => new()
    {
        ClientHostId = Client,
        Features = [InstallationFeature.TypedConversation, InstallationFeature.MicrophoneInput, InstallationFeature.SpokenReplies],
        Hosts = [new(Client, "Client", Capacity), new(Host, "Kitchen Ubuntu", Capacity)],
        Runtimes = [
            new(Id(20), null, "named-api", "api-tuple-v1", InstallationRuntimeKind.ExternalApi, External, Eligible, [], []),
            new(Id(21), Host, "named-worker", "worker-tuple-v1", InstallationRuntimeKind.Compose, Managed, Eligible, [Id(30)], HostFacts),
            new(Id(22), Client, "client-audio", "audio-tuple-v1", InstallationRuntimeKind.Native, External, Eligible, [Id(31)],
                HostFacts.Where(p => p.Kind is InstallationPrerequisite.HostQualification or InstallationPrerequisite.RuntimeCompatibility).ToImmutableArray())
        ],
        Resources = [
            Resource(30, "worker", Small, []),
            new(Id(31), Client, "audio", External, new(100, 100, 0, 0), false, Eligible, [])
        ],
        Destinations = [
            Destination(InstallationRole.Llm, Id(20)), Destination(InstallationRole.Stt, Id(21)), Destination(InstallationRole.Tts, Id(21)),
            Destination(InstallationRole.Capture, Id(22)), Destination(InstallationRole.Output, Id(22))
        ]
    };
}
