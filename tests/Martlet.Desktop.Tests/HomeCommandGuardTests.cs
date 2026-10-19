using Martlet.Home;

namespace Martlet.Desktop.Tests;

public sealed class HomeCommandGuardTests
{
    [Theory]
    [InlineData("Why don't you unlock the front door")]
    [InlineData("How about opening the garage door")]
    [InlineData("Do unlock the back door")]
    [InlineData("Have the alarm disarmed")]
    [InlineData("Est-ce que tu peux déverrouiller la porte ?")]
    [InlineData("why don't you open the gate")]
    [InlineData("Warum öffnest du nicht die Haustür?")]
    [InlineData("Unlock the front door")]
    [InlineData("Désarme l'alarme")]
    [InlineData("Hey Martlet, why not disarm the alarm")]
    [InlineData("¿Por qué no abres la puerta?")]
    [InlineData("Did I unlock the door?")]
    public void RequestsThatOpenTheHomeAreSensitiveAlsoWhenTheyStartWithAQuestionWord(string text) =>
        Assert.Equal(HomeCommandRisk.Sensitive, HomeCommandGuard.Assess(text));

    [Theory]
    [InlineData("Is the front door locked?")]
    [InlineData("Is the garage door open?")]
    [InlineData("Are the gates still open?")]
    [InlineData("Did I lock the door?")]
    [InlineData("Did I close the garage?")]
    [InlineData("What's the alarm status?")]
    [InlineData("Is the lock on?")]
    [InlineData("Hey Martlet, is the back door unlocked?")]
    [InlineData("Est-ce que la porte est verrouillée ?")]
    [InlineData("Ist die Haustür offen?")]
    [InlineData("Turn on the kitchen lights")]
    public void StatusQuestionsAndOrdinaryRequestsStayNormal(string text) =>
        Assert.Equal(HomeCommandRisk.Normal, HomeCommandGuard.Assess(text));

    [Fact]
    public void LongSentencesThatMentionADoorAreChat() =>
        Assert.Equal(HomeCommandRisk.NotACommand,
            HomeCommandGuard.Assess("I walked past the old door in town today and it reminded me of my grandmother's house so much"));
}
