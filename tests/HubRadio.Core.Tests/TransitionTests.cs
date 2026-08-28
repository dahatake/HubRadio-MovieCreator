using System.Reflection;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TransitionTests
{
    [TestMethod]
    public void ContractsExposeExactEnumsFactoriesAndGetOnlyProperties()
    {
        AssertExactEnum<TransitionKind>(
            "Cut",
            "Crossfade",
            "Fade",
            "Wipe",
            "Slide",
            "TitleCard",
            "Zoom");
        AssertExactEnum<TransitionDirection>("Left", "Right", "Up", "Down");

        Type transitionType = typeof(Transition);
        Assert.IsTrue(transitionType.IsPublic);
        Assert.IsTrue(transitionType.IsClass);
        Assert.IsTrue(transitionType.IsSealed);
        Assert.IsFalse(transitionType.IsAbstract);
        Assert.IsTrue(typeof(IEquatable<Transition>).IsAssignableFrom(transitionType));

        ConstructorInfo[] constructors = transitionType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.AreEqual(1, constructors.Length);
        Assert.IsTrue(constructors[0].IsPrivate);

        AssertPublicGetOnlyProperties(transitionType, "Kind", "Duration", "Direction");
        AssertFactory(nameof(Transition.Cut));
        AssertFactory(nameof(Transition.Crossfade), typeof(RationalTime));
        AssertFactory(nameof(Transition.Fade), typeof(RationalTime));
        AssertFactory(
            nameof(Transition.Wipe),
            typeof(RationalTime),
            typeof(TransitionDirection));
        AssertFactory(
            nameof(Transition.Slide),
            typeof(RationalTime),
            typeof(TransitionDirection));
        AssertFactory(nameof(Transition.TitleCard), typeof(RationalTime));
        AssertFactory(nameof(Transition.Zoom), typeof(RationalTime));

        MethodInfo[] factories = transitionType.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "Crossfade", "Cut", "Fade", "Slide", "TitleCard", "Wipe", "Zoom" },
            factories.Select(static method => method.Name).ToArray());
    }

    [TestMethod]
    public void FactoriesStoreOnlyPropertiesValidForTheirKinds()
    {
        RationalTime duration = new(17, 29);

        AssertTransition(Transition.Cut(), TransitionKind.Cut, default, null);
        AssertTransition(
            Transition.Crossfade(duration),
            TransitionKind.Crossfade,
            duration,
            null);
        AssertTransition(Transition.Fade(duration), TransitionKind.Fade, duration, null);
        AssertTransition(
            Transition.Wipe(duration, TransitionDirection.Left),
            TransitionKind.Wipe,
            duration,
            TransitionDirection.Left);
        AssertTransition(
            Transition.Slide(duration, TransitionDirection.Down),
            TransitionKind.Slide,
            duration,
            TransitionDirection.Down);
        AssertTransition(
            Transition.TitleCard(duration),
            TransitionKind.TitleCard,
            duration,
            null);
        AssertTransition(Transition.Zoom(duration), TransitionKind.Zoom, duration, null);
    }

    [TestMethod]
    public void CutIsExactlyCanonicalZeroAndEffectsPreserveExactRationalDuration()
    {
        Transition cut = Transition.Cut();
        RationalTime exactDuration = new(long.MaxValue - 1, long.MaxValue);

        Assert.AreEqual(default(RationalTime), cut.Duration);
        Assert.AreEqual(0L, cut.Duration.Value);
        Assert.AreEqual(1L, cut.Duration.Rate);

        foreach (Transition effect in CreateEffects(exactDuration))
        {
            Assert.AreEqual(exactDuration, effect.Duration);
            Assert.AreEqual(long.MaxValue - 1, effect.Duration.Value);
            Assert.AreEqual(long.MaxValue, effect.Duration.Rate);
            Assert.IsTrue(effect.Duration > default(RationalTime));
        }
    }

    [TestMethod]
    public void EffectFactoriesRejectZeroAndNegativeDurations()
    {
        foreach (RationalTime duration in new[] { default, new RationalTime(-1, 2) })
        {
            foreach (Func<Transition> create in EffectFactories(duration))
            {
                ArgumentOutOfRangeException exception =
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => create());
                Assert.AreEqual("duration", exception.ParamName);
            }
        }
    }

    [TestMethod]
    public void DirectionalFactoriesRejectUndefinedDirections()
    {
        RationalTime duration = new(1, 2);
        TransitionDirection[] invalidDirections =
        [
            (TransitionDirection)(-1),
            (TransitionDirection)int.MaxValue,
        ];

        foreach (TransitionDirection direction in invalidDirections)
        {
            AssertParamName<ArgumentOutOfRangeException>(
                "direction",
                () => Transition.Wipe(duration, direction));
            AssertParamName<ArgumentOutOfRangeException>(
                "direction",
                () => Transition.Slide(duration, direction));
        }
    }

    [TestMethod]
    public void EquivalentTransitionsUseValueEqualityAndStableHashCodes()
    {
        Transition first = Transition.Wipe(new RationalTime(2, 4), TransitionDirection.Right);
        Transition equivalent = Transition.Wipe(
            new RationalTime(500, 1000),
            TransitionDirection.Right);

        Assert.AreNotSame(first, equivalent);
        Assert.IsTrue(first.Equals(equivalent));
        Assert.IsTrue(first.Equals((object)equivalent));
        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.IsFalse(first.Equals(null));
        Assert.IsFalse(first.Equals((object?)null));
        Assert.IsFalse(first.Equals(Transition.Wipe(new RationalTime(1, 3), TransitionDirection.Right)));
        Assert.IsFalse(first.Equals(Transition.Wipe(new RationalTime(1, 2), TransitionDirection.Left)));
        Assert.IsFalse(first.Equals(Transition.Slide(new RationalTime(1, 2), TransitionDirection.Right)));
        Assert.IsFalse(first.Equals("wipe"));
    }

    [TestMethod]
    public void PublicSurfaceContainsNoArbitraryStringsOrAutomaticSelection()
    {
        Type transitionType = typeof(Transition);
        MemberInfo[] declaredMembers = transitionType.GetMembers(
            BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly);

        Assert.IsFalse(declaredMembers.Any(static member =>
            member.Name.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Automatic", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(transitionType.GetProperties().Any(static property =>
            property.PropertyType == typeof(string)));
        Assert.IsFalse(transitionType.GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly)
            .Any(static method =>
                method.ReturnType == typeof(string) ||
                method.GetParameters().Any(static parameter =>
                    parameter.ParameterType == typeof(string))));
    }

    private static Transition[] CreateEffects(RationalTime duration) =>
    [
        Transition.Crossfade(duration),
        Transition.Fade(duration),
        Transition.Wipe(duration, TransitionDirection.Up),
        Transition.Slide(duration, TransitionDirection.Down),
        Transition.TitleCard(duration),
        Transition.Zoom(duration),
    ];

    private static Func<Transition>[] EffectFactories(RationalTime duration) =>
    [
        () => Transition.Crossfade(duration),
        () => Transition.Fade(duration),
        () => Transition.Wipe(duration, TransitionDirection.Left),
        () => Transition.Slide(duration, TransitionDirection.Right),
        () => Transition.TitleCard(duration),
        () => Transition.Zoom(duration),
    ];

    private static void AssertTransition(
        Transition transition,
        TransitionKind expectedKind,
        RationalTime expectedDuration,
        TransitionDirection? expectedDirection)
    {
        Assert.AreEqual(expectedKind, transition.Kind);
        Assert.AreEqual(expectedDuration, transition.Duration);
        Assert.AreEqual(expectedDirection, transition.Direction);
    }

    private static void AssertExactEnum<TEnum>(params string[] expectedNames)
        where TEnum : struct, Enum
    {
        CollectionAssert.AreEqual(expectedNames, Enum.GetNames<TEnum>());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, expectedNames.Length).ToArray(),
            Enum.GetValues<TEnum>().Select(static value => Convert.ToInt32(value)).ToArray());
    }

    private static void AssertFactory(string name, params Type[] parameterTypes)
    {
        MethodInfo[] methods = typeof(Transition).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name)
            .ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(typeof(Transition), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            parameterTypes,
            methods[0].GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}