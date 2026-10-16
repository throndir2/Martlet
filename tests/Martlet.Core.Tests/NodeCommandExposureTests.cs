using Martlet.Core.Contracts;
using Martlet.Core.Nodes;

namespace Martlet.Core.Tests;

public sealed class NodeCommandExposureTests
{
    private static Dictionary<string, string> Arguments(string outside, string codes = "no", string all = "yes") => new(StringComparer.Ordinal)
    {
        ["outside"] = outside, ["pairing_codes_outside"] = codes, ["treat_all_as_outside"] = all
    };

    [Fact]
    public void Exposure_arguments_become_exactly_the_engine_options()
    {
        var arguments = NodeCommandRules.ExposureArguments([" https://GPU-Box.tailnet.ts.net:9443/ ", "100.101.102.103:9443"], true, false);
        Assert.Equal("gpu-box.tailnet.ts.net:9443,100.101.102.103:9443", arguments["outside"]);
        Assert.Equal(["--outside", "gpu-box.tailnet.ts.net:9443", "--outside", "100.101.102.103:9443",
            "--allow-pairing-outside-home", "yes", "--treat-all-as-outside", "no"], NodeCommandRules.ExposureOptions(arguments));
        Assert.Equal(["--clear-outside", "--allow-pairing-outside-home", "no", "--treat-all-as-outside", "yes"],
            NodeCommandRules.ExposureOptions(Arguments("")));
        Assert.Contains(NodeCommandKinds.Exposure, NodeCommandKinds.All);
    }

    [Theory]
    [InlineData("evil;reboot:1")]
    [InlineData("Home.Example.net:9443")]
    [InlineData("a.example:1,a.example:1")]
    [InlineData("a.example:1,b.example:1,c.example:1,d.example:1,e.example:1")]
    [InlineData("a.example:1 --config /etc")]
    public void Exposure_refuses_anything_but_canonical_addresses(string outside) =>
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure, Arguments(outside), new Dictionary<string, string>()));

    [Fact]
    public void Exposure_refuses_other_arguments_values_and_secrets()
    {
        var empty = new Dictionary<string, string>();
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure, Arguments("", codes: "maybe"), empty));
        var extra = Arguments("");
        extra["config"] = "/etc";
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure, extra, empty));
        var missing = Arguments("");
        missing.Remove("treat_all_as_outside");
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure, missing, empty));
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure, Arguments(""),
            new Dictionary<string, string> { ["secret.x"] = "y" }));
        NodeCommandRules.Validate(NodeCommandKinds.Exposure, Arguments(""), empty);
    }

    [Fact]
    public void The_front_door_opens_and_closes_alone_or_with_outside_access()
    {
        Assert.Equal(["--front-door", "9445"], NodeCommandRules.ExposureOptions(NodeCommandRules.FrontDoorArguments(9445)));
        Assert.Equal(["--front-door", "off"], NodeCommandRules.ExposureOptions(NodeCommandRules.FrontDoorArguments(null)));
        var both = Arguments("");
        both["front_door"] = "8443";
        Assert.Equal(["--clear-outside", "--allow-pairing-outside-home", "no", "--treat-all-as-outside", "yes", "--front-door", "8443"],
            NodeCommandRules.ExposureOptions(both));
    }

    [Theory]
    [InlineData("80")]
    [InlineData("65536")]
    [InlineData("09445")]
    [InlineData("+9445")]
    [InlineData("9445 --config /etc")]
    [InlineData("on")]
    [InlineData("")]
    public void The_front_door_takes_only_a_port_from_1024_or_off(string port)
    {
        var empty = new Dictionary<string, string>();
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure,
            new Dictionary<string, string> { ["front_door"] = port }, empty));
        Assert.Throws<ContractException>(() => NodeCommandRules.FrontDoorArguments(80));
        // Half of the outside trio with a front door is still refused.
        Assert.Throws<ContractException>(() => NodeCommandRules.Validate(NodeCommandKinds.Exposure,
            new Dictionary<string, string> { ["front_door"] = "9445", ["outside"] = "" }, empty));
    }
}