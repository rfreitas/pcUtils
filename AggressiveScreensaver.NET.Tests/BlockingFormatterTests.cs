using AggressiveScreensaver.Parsing;

namespace AggressiveScreensaver.NET.Tests;

public class BlockingFormatterTests
{
    [Fact] public void EmptyList_ReturnsEmptyString()      => Assert.Equal("", BlockingFormatter.Format([]));
    [Fact] public void OneItem_ReturnsThatItem()           => Assert.Equal("foo", BlockingFormatter.Format(["foo"]));
    [Fact] public void TwoItems_ReturnsBothCommaJoined()   => Assert.Equal("foo, bar", BlockingFormatter.Format(["foo", "bar"]));
    [Fact] public void ThreeItems_ShowsTwoPlusMore()       => Assert.Equal("foo, bar +1 more", BlockingFormatter.Format(["foo", "bar", "baz"]));
    [Fact] public void FiveItems_ShowsTwoPlusFourMore()    => Assert.Equal("a, b +3 more", BlockingFormatter.Format(["a", "b", "c", "d", "e"]));
}
