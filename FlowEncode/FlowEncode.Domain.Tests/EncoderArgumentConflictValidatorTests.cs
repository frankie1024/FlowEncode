using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncoderArgumentConflictValidatorTests
{
    [TestMethod]
    public void FindFirstConflict_WhenSameOptionHasDifferentValues_ReturnsConflictingValues()
    {
        // Arrange
        const string arguments = "--keyint 240 --keyint 250";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X264, arguments, string.Empty);

        // Assert
        Assert.IsNotNull(conflict);
        Assert.AreEqual(EncoderArgumentConflictKind.ConflictingValues, conflict!.Kind);
        Assert.AreEqual("--keyint", conflict.OptionName);
        Assert.AreEqual("240", conflict.FirstValue);
        Assert.AreEqual("250", conflict.SecondValue);
        Assert.AreEqual(EncoderArgumentSource.AdditionalArguments, conflict.FirstSource);
        Assert.AreEqual(EncoderArgumentSource.AdditionalArguments, conflict.SecondSource);
    }

    [TestMethod]
    public void FindFirstConflict_WhenOppositeSwitchesAppearAcrossFields_ReturnsOppositeSwitches()
    {
        // Arrange
        const string additionalArguments = "--sao";
        const string uhdParameters = "--no-sao";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X265, additionalArguments, uhdParameters);

        // Assert
        Assert.IsNotNull(conflict);
        Assert.AreEqual(EncoderArgumentConflictKind.OppositeSwitches, conflict!.Kind);
        Assert.AreEqual("--sao", conflict.OptionName);
        Assert.AreEqual("--sao", conflict.FirstOptionName);
        Assert.AreEqual("--no-sao", conflict.SecondOptionName);
        Assert.AreEqual(EncoderArgumentSource.AdditionalArguments, conflict.FirstSource);
        Assert.AreEqual(EncoderArgumentSource.UhdParameters, conflict.SecondSource);
    }

    [TestMethod]
    public void FindFirstConflict_WhenInlineAndSplitValuesDisagree_ReturnsConflictingValues()
    {
        // Arrange
        const string additionalArguments = "--colorprim=bt709";
        const string uhdParameters = "--colorprim bt2020";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X265, additionalArguments, uhdParameters);

        // Assert
        Assert.IsNotNull(conflict);
        Assert.AreEqual(EncoderArgumentConflictKind.ConflictingValues, conflict!.Kind);
        Assert.AreEqual("--colorprim", conflict.OptionName);
        Assert.AreEqual("bt709", conflict.FirstValue);
        Assert.AreEqual("bt2020", conflict.SecondValue);
    }

    [TestMethod]
    public void FindFirstConflict_WhenBooleanValuesDisagree_ReturnsConflictingValues()
    {
        // Arrange
        const string arguments = "--enable-overlays 1 --enable-overlays=0";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.SvtAv1, arguments, string.Empty);

        // Assert
        Assert.IsNotNull(conflict);
        Assert.AreEqual(EncoderArgumentConflictKind.ConflictingValues, conflict!.Kind);
        Assert.AreEqual("--enable-overlays", conflict.OptionName);
        Assert.AreEqual("1", conflict.FirstValue);
        Assert.AreEqual("0", conflict.SecondValue);
    }

    [TestMethod]
    public void FindFirstConflict_WhenQuotedInlineAndSplitValuesDisagree_ReturnsConflictingValues()
    {
        // Arrange
        const string additionalArguments = "--master-display=\"G(1 2)\"";
        const string uhdParameters = "--master-display \"G(3 4)\"";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X265, additionalArguments, uhdParameters);

        // Assert
        Assert.IsNotNull(conflict);
        Assert.AreEqual(EncoderArgumentConflictKind.ConflictingValues, conflict!.Kind);
        Assert.AreEqual("--master-display", conflict.OptionName);
        Assert.AreEqual("G(1 2)", conflict.FirstValue);
        Assert.AreEqual("G(3 4)", conflict.SecondValue);
    }

    [TestMethod]
    public void FindFirstConflict_WhenRepeatedValueMatches_ReturnsNull()
    {
        // Arrange
        const string additionalArguments = "--colorprim bt2020";
        const string uhdParameters = "--colorprim bt2020";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X265, additionalArguments, uhdParameters);

        // Assert
        Assert.IsNull(conflict);
    }

    [TestMethod]
    public void FindFirstConflict_WhenNonX265IgnoresUhdField_ReturnsNull()
    {
        // Arrange
        const string additionalArguments = "--keyint 240";
        const string uhdParameters = "--keyint 250";

        // Act
        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(EncoderKind.X264, additionalArguments, uhdParameters);

        // Assert
        Assert.IsNull(conflict);
    }
}
