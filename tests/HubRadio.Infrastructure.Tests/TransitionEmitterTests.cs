using System.Reflection;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class TransitionEmitterTests
{
    [TestMethod]
    public void ContractsExposeExactOperationAndMinimalImmutableTypedSurface()
    {
        CollectionAssert.AreEqual(
            new[] { "DirectCut", "Crossfade", "Fade", "Wipe", "Slide", "TitleCard", "Zoom" },
            Enum.GetNames<TransitionRenderOperation>());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 7).ToArray(),
            Enum.GetValues<TransitionRenderOperation>()
                .Select(static value => (int)value)
                .ToArray());

        Type instructionType = typeof(TransitionRenderInstruction);
        Assert.IsTrue(instructionType.IsPublic);
        Assert.IsTrue(instructionType.IsSealed);
        Assert.IsFalse(instructionType.IsAbstract);
        ConstructorInfo[] instructionConstructors = instructionType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.AreEqual(1, instructionConstructors.Length);
        Assert.IsTrue(instructionConstructors[0].IsAssembly);
        AssertPublicGetOnlyProperties(
            instructionType,
            "Operation",
            "Duration",
            "Direction");

        Type emitterType = typeof(TransitionEmitter);
        Assert.IsTrue(emitterType.IsPublic);
        Assert.IsTrue(emitterType.IsClass);
        Assert.IsTrue(emitterType.IsSealed);
        Assert.IsFalse(emitterType.IsAbstract);
        ConstructorInfo constructor = emitterType.GetConstructors().Single();
        Assert.AreEqual(0, constructor.GetParameters().Length);

        MethodInfo[] methods = emitterType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(nameof(TransitionEmitter.Emit), methods[0].Name);
        Assert.AreEqual(typeof(TransitionRenderInstruction), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(Transition) },
            methods[0].GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    [TestMethod]
    public void EmitMapsEveryTransitionToTheExactTypedOperation()
    {
        RationalTime duration = new(17, 29);
        TransitionEmitter emitter = new();
        (Transition Transition, TransitionRenderOperation Operation)[] cases =
        [
            (Transition.Cut(), TransitionRenderOperation.DirectCut),
            (Transition.Crossfade(duration), TransitionRenderOperation.Crossfade),
            (Transition.Fade(duration), TransitionRenderOperation.Fade),
            (Transition.Wipe(duration, TransitionDirection.Left), TransitionRenderOperation.Wipe),
            (Transition.Slide(duration, TransitionDirection.Down), TransitionRenderOperation.Slide),
            (Transition.TitleCard(duration), TransitionRenderOperation.TitleCard),
            (Transition.Zoom(duration), TransitionRenderOperation.Zoom),
        ];

        foreach ((Transition transition, TransitionRenderOperation operation) in cases)
        {
            TransitionRenderInstruction instruction = emitter.Emit(transition);
            Assert.AreEqual(operation, instruction.Operation);
            Assert.AreEqual(transition.Duration, instruction.Duration);
            Assert.AreEqual(transition.Direction, instruction.Direction);
        }
    }

    [TestMethod]
    public void EmitPreservesEveryDirectionAndSameExactRationalDuration()
    {
        RationalTime exactDuration = new(long.MaxValue - 1, long.MaxValue);
        TransitionEmitter emitter = new();

        foreach (TransitionDirection direction in Enum.GetValues<TransitionDirection>())
        {
            TransitionRenderInstruction wipe = emitter.Emit(
                Transition.Wipe(exactDuration, direction));
            TransitionRenderInstruction slide = emitter.Emit(
                Transition.Slide(exactDuration, direction));

            Assert.AreEqual(direction, wipe.Direction);
            Assert.AreEqual(direction, slide.Direction);
            Assert.AreEqual(exactDuration, wipe.Duration);
            Assert.AreEqual(exactDuration, slide.Duration);
            Assert.AreEqual(long.MaxValue - 1, wipe.Duration.Value);
            Assert.AreEqual(long.MaxValue, wipe.Duration.Rate);
            Assert.AreEqual(long.MaxValue - 1, slide.Duration.Value);
            Assert.AreEqual(long.MaxValue, slide.Duration.Rate);
        }
    }

    [TestMethod]
    public void ExplicitCutAlwaysEmitsDirectCutWithoutAutomaticSelection()
    {
        TransitionRenderInstruction instruction = new TransitionEmitter().Emit(Transition.Cut());

        Assert.AreEqual(TransitionRenderOperation.DirectCut, instruction.Operation);
        Assert.AreEqual(default(RationalTime), instruction.Duration);
        Assert.IsNull(instruction.Direction);

        Assert.IsFalse(typeof(TransitionEmitter).GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly)
            .Any(static method =>
                method.Name.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Automatic", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void EmitRejectsNullTransition()
    {
        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(
            () => new TransitionEmitter().Emit(null!));

        Assert.AreEqual("transition", exception.ParamName);
    }

    [TestMethod]
    public void InstructionRejectsUndefinedOperationAndInvalidDurationCombinations()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "operation",
            () => new TransitionRenderInstruction(
                (TransitionRenderOperation)int.MaxValue,
                default,
                null));
        AssertParamName<ArgumentOutOfRangeException>(
            "duration",
            () => new TransitionRenderInstruction(
                TransitionRenderOperation.DirectCut,
                new RationalTime(1, 2),
                null));

        foreach (TransitionRenderOperation operation in Enum.GetValues<TransitionRenderOperation>()
            .Where(static operation => operation != TransitionRenderOperation.DirectCut))
        {
            TransitionDirection? direction = operation is
                TransitionRenderOperation.Wipe or TransitionRenderOperation.Slide
                ? TransitionDirection.Left
                : null;
            foreach (RationalTime duration in new[] { default, new RationalTime(-1, 2) })
            {
                AssertParamName<ArgumentOutOfRangeException>(
                    "duration",
                    () => new TransitionRenderInstruction(operation, duration, direction));
            }
        }
    }

    [TestMethod]
    public void InstructionAllowsDirectionOnlyForWipeAndSlide()
    {
        RationalTime duration = new(1, 2);

        foreach (TransitionRenderOperation operation in new[]
        {
            TransitionRenderOperation.Wipe,
            TransitionRenderOperation.Slide,
        })
        {
            AssertParamName<ArgumentException>(
                "direction",
                () => new TransitionRenderInstruction(operation, duration, null));
            AssertParamName<ArgumentOutOfRangeException>(
                "direction",
                () => new TransitionRenderInstruction(
                    operation,
                    duration,
                    (TransitionDirection)int.MaxValue));
        }

        AssertParamName<ArgumentException>(
            "direction",
            () => new TransitionRenderInstruction(
                TransitionRenderOperation.DirectCut,
                default,
                TransitionDirection.Left));
        foreach (TransitionRenderOperation operation in new[]
        {
            TransitionRenderOperation.Crossfade,
            TransitionRenderOperation.Fade,
            TransitionRenderOperation.TitleCard,
            TransitionRenderOperation.Zoom,
        })
        {
            AssertParamName<ArgumentException>(
                "direction",
                () => new TransitionRenderInstruction(
                    operation,
                    duration,
                    TransitionDirection.Left));
        }
    }

    [TestMethod]
    public void BoundaryContainsNoStringFilterShellOrProcessSurface()
    {
        Type[] boundaryTypes =
        [
            typeof(TransitionRenderOperation),
            typeof(TransitionRenderInstruction),
            typeof(TransitionEmitter),
        ];
        string[] prohibitedNames = ["Filter", "Ffmpeg", "Argument", "Command", "Shell", "Process"];

        foreach (Type type in boundaryTypes)
        {
            MemberInfo[] members = type.GetMembers(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly);
            Assert.IsFalse(members.Any(member => prohibitedNames.Any(name =>
                member.Name.Contains(name, StringComparison.OrdinalIgnoreCase))));

            Assert.IsFalse(type.GetProperties(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Any(static property => property.PropertyType == typeof(string)));
            Assert.IsFalse(type.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(static constructor => constructor.GetParameters().Any(
                    static parameter => IsForbiddenBoundaryType(parameter.ParameterType))));
            Assert.IsFalse(type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Any(static method =>
                    IsForbiddenBoundaryType(method.ReturnType) ||
                    method.GetParameters().Any(static parameter =>
                        IsForbiddenBoundaryType(parameter.ParameterType))));
        }
    }

    private static bool IsForbiddenBoundaryType(Type type)
    {
        return type == typeof(string) ||
            type.FullName?.Contains("Filter", StringComparison.OrdinalIgnoreCase) == true ||
            type.FullName?.Contains("Shell", StringComparison.OrdinalIgnoreCase) == true ||
            type.FullName?.Contains("Process", StringComparison.OrdinalIgnoreCase) == true;
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