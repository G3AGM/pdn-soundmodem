using AwesomeAssertions;
using Packet.SoundModem.Updates;

namespace Packet.SoundModem.Tests.Updates;

/// <summary>
/// Debian's version ordering (dpkg's verrevcmp), on the cases that are easy to get wrong: a
/// multi-digit upstream segment, an epoch, a tilde pre-release, a revision and a local build
/// suffix. <see cref="UpdateCheckTests"/> covers the check that uses this against a real-shaped
/// index; this pins the comparison itself.
/// </summary>
public class DebianVersionTests
{
    [Fact]
    public void A_Two_Digit_Upstream_Segment_Beats_A_One_Digit_One_Numerically_Not_Lexically()
    {
        // Pure string comparison would say "0.88.9" > "0.88.10" (9 > 1); dpkg compares the runs
        // of digits as numbers, so the second release of the month is correctly the newer one.
        DebianVersion.Compare("0.88.10", "0.88.9").Should().BePositive();
        DebianVersion.Compare("0.88.9", "0.88.10").Should().BeNegative();
    }

    [Fact]
    public void An_Epoch_Outranks_Everything_After_It()
    {
        DebianVersion.Compare("1:0.1", "0.99").Should().BePositive(
            "an epoch of 1 beats any upstream version with no epoch (epoch 0), whatever the digits say");
        DebianVersion.Compare("0.99", "1:0.1").Should().BeNegative();
    }

    [Fact]
    public void A_Tilde_Pre_Release_Sorts_Before_The_Release_It_Precedes()
    {
        DebianVersion.Compare("0.89.0~rc1", "0.89.0").Should().BeNegative(
            "a tilde sorts before everything, even the end of the string, so an rc is older than the release it leads up to");
        DebianVersion.Compare("0.89.0", "0.89.0~rc1").Should().BePositive();
    }

    [Fact]
    public void A_Later_Revision_Of_The_Same_Upstream_Version_Is_Newer()
    {
        DebianVersion.Compare("0.88.0-2", "0.88.0-1").Should().BePositive();
        DebianVersion.Compare("0.88.0-1", "0.88.0-2").Should().BeNegative();
    }

    [Fact]
    public void A_Local_Build_Suffix_Sorts_After_The_Bare_Version_It_Was_Built_From()
    {
        // dpkg's real-world convention for a local suffix such as "+deb12u1" or "+dev": it reads
        // as later than the version it was appended to, not equal to it and not earlier.
        DebianVersion.Compare("0.88.0+dev", "0.88.0").Should().BePositive();
        DebianVersion.Compare("0.88.0", "0.88.0+dev").Should().BeNegative();
    }

    [Fact]
    public void The_Same_Version_Compares_Equal()
    {
        DebianVersion.Compare("0.88.0", "0.88.0").Should().Be(0);
        DebianVersion.Compare("1:0.88.0-2", "1:0.88.0-2").Should().Be(0);
    }
}
