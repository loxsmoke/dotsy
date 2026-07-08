using Dotsy.Core.Utils;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class StringExtensionsTests
{
    #region EqualsNoCase

    [TestMethod]
    public void EqualsNoCase_ReturnsFalse_ForNullAndNonNull()
    {
        Assert.IsFalse(((string?)null).EqualsNoCase("hello"));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsTrue_ForTwoNulls()
    {
        Assert.IsTrue(((string?)null).EqualsNoCase(null));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsTrue_ForIdenticalStrings()
    {
        Assert.IsTrue("hello".EqualsNoCase("hello"));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsTrue_ForDifferentCase()
    {
        Assert.IsTrue("Hello".EqualsNoCase("hello"));
        Assert.IsTrue("HELLO".EqualsNoCase("hello"));
        Assert.IsTrue("hello".EqualsNoCase("HELLO"));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsFalse_ForDifferentStrings()
    {
        Assert.IsFalse("hello".EqualsNoCase("world"));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsFalse_ForEmptyAndNonEmpty()
    {
        Assert.IsFalse("".EqualsNoCase("hello"));
        Assert.IsFalse("hello".EqualsNoCase(""));
    }

    [TestMethod]
    public void EqualsNoCase_ReturnsTrue_ForTwoEmptyStrings()
    {
        Assert.IsTrue("".EqualsNoCase(""));
    }

    #endregion

    #region StartsWithNoCase

    [TestMethod]
    public void StartsWithNoCase_ReturnsTrue_ForMatchingPrefix()
    {
        Assert.IsTrue("Hello World".StartsWithNoCase("hello"));
    }

    [TestMethod]
    public void StartsWithNoCase_ReturnsTrue_ForUpperCasePrefix()
    {
        Assert.IsTrue("hello world".StartsWithNoCase("HELLO"));
    }

    [TestMethod]
    public void StartsWithNoCase_ReturnsTrue_ForEmptyPrefix()
    {
        Assert.IsTrue("hello".StartsWithNoCase(""));
    }

    [TestMethod]
    public void StartsWithNoCase_ReturnsFalse_ForNonMatchingPrefix()
    {
        Assert.IsFalse("hello world".StartsWithNoCase("world"));
    }

    [TestMethod]
    public void StartsWithNoCase_ReturnsTrue_ForFullStringAsPrefix()
    {
        Assert.IsTrue("hello".StartsWithNoCase("hello"));
    }

    #endregion

    #region EndsWithNoCase

    [TestMethod]
    public void EndsWithNoCase_ReturnsTrue_ForMatchingSuffix()
    {
        Assert.IsTrue("Hello World".EndsWithNoCase("world"));
    }

    [TestMethod]
    public void EndsWithNoCase_ReturnsTrue_ForUpperCaseSuffix()
    {
        Assert.IsTrue("hello world".EndsWithNoCase("WORLD"));
    }

    [TestMethod]
    public void EndsWithNoCase_ReturnsTrue_ForEmptySuffix()
    {
        Assert.IsTrue("hello".EndsWithNoCase(""));
    }

    [TestMethod]
    public void EndsWithNoCase_ReturnsFalse_ForNonMatchingSuffix()
    {
        Assert.IsFalse("hello world".EndsWithNoCase("hello"));
    }

    [TestMethod]
    public void EndsWithNoCase_ReturnsTrue_ForFullStringAsSuffix()
    {
        Assert.IsTrue("hello".EndsWithNoCase("hello"));
    }

    #endregion

    #region ContainsNoCase

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForMatchingSubstring()
    {
        Assert.IsTrue("Hello World".ContainsNoCase("lo wo"));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForUpperCaseSubstring()
    {
        Assert.IsTrue("hello world".ContainsNoCase("LO WO"));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForEmptySubstring()
    {
        Assert.IsTrue("hello".ContainsNoCase(""));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsFalse_ForNonMatchingSubstring()
    {
        Assert.IsFalse("hello world".ContainsNoCase("xyz"));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForFullStringAsSubstring()
    {
        Assert.IsTrue("hello".ContainsNoCase("hello"));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForSubstringAtStart()
    {
        Assert.IsTrue("hello world".ContainsNoCase("HELLO"));
    }

    [TestMethod]
    public void ContainsNoCase_ReturnsTrue_ForSubstringAtEnd()
    {
        Assert.IsTrue("hello world".ContainsNoCase("WORLD"));
    }

    #endregion
}
