#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using StateSmith.SmGraph;
using System.IO;
using StateSmith.Input;
using StateSmith.Input.PlantUML;
using StateSmith.Input.Yed;
using StateSmith.Input.DrawIo;
using System.Diagnostics.CodeAnalysis;
using StateSmith.Common;
using StateSmith.Output.Algos.Balanced1;

namespace StateSmith.Runner;

/// <summary>
/// This class converts an input diagram/design into a StateMachine vertex and finishes building/transforming
/// the StateMachine vertex so that is ready for code generation.
/// 
/// Step 1: create StateMachine vertex (or more) from input (like a diagram). 
/// Some diagram types (other than PlantUML) can have multiple StateMachines.
/// 
/// Step 2: select the StateMachine to build. Optional if input only has a single state machine design.
/// 
/// Step 3: finish building/transforming the selected StateMachine vertex so that is ready for code generation.
/// </summary>
public class InputSmBuilder
{
    public readonly SmTransformer transformer;

    public StateMachine GetStateMachine() => Sm.ThrowIfNull();

    protected StateMachine? Sm { get; set; }

    internal DiagramToSmConverter diagramToSmConverter; // todo - rework unit test code that relies on this so that it can be private https://github.com/StateSmith/StateSmith/issues/97
    internal DiServiceProvider sp; // todo - rework unit test code that relies on this so we can remove it https://github.com/StateSmith/StateSmith/issues/97

    readonly NameMangler mangler;
    readonly DrawIoToSmDiagramConverter drawIoConverter;
    readonly StateMachineProvider stateMachineProvider;

    public InputSmBuilder(SmTransformer transformer, DiagramToSmConverter diagramToSmConverter, NameMangler mangler, DrawIoToSmDiagramConverter converter, DiServiceProvider sp, StateMachineProvider stateMachineProvider)
    {
        SmRunnerInternal.AppUseDecimalPeriod(); // done here as well to help with unit tests

        this.transformer = transformer;
        this.diagramToSmConverter = diagramToSmConverter;
        this.mangler = mangler;
        this.drawIoConverter = converter;
        this.sp = sp;
        this.stateMachineProvider = stateMachineProvider;
    }

    // todo_low - replace with unit testing factory helper.
    // The factory helper could setup DI and then this class could rely on it.
    internal InputSmBuilder(Action<DiServiceProvider>? setupAction = null)
    {
        SmRunnerInternal.AppUseDecimalPeriod(); // done here as well to help with unit tests

        sp = DiServiceProvider.CreateDefault();
        sp.AddSingleton(this);
        setupAction?.Invoke(sp);
        sp.Build();
        diagramToSmConverter = sp.GetServiceOrCreateInstance();
        transformer = sp.GetServiceOrCreateInstance();
        mangler = sp.GetInstanceOf<NameMangler>();
        drawIoConverter = sp.GetServiceOrCreateInstance();
        stateMachineProvider = sp.GetServiceOrCreateInstance();
    }

    /// <summary>
    /// Step 1. Figures out how to parse file based on file name.
    /// </summary>
    public void ConvertDiagramFileToSmVertices(string diagramFile)
    {
        var fileExtension = Path.GetExtension(diagramFile).ToLower();
        FileAssociator fileAssociator = new();

        if (fileAssociator.IsYedExtension(fileExtension))
        {
            ConvertYedFileNodesToVertices(diagramFile);
        }
        else if (fileAssociator.IsPlantUmlExtension(fileExtension))
        {
            ConvertPlantUmlFileNodesToVertices(diagramFile);
        }
        else if (fileAssociator.IsDrawIoFile(diagramFile)) // needs full diagram file name to support double extension like: `my_file.drawio.svg`
        {
            ConvertDrawIoFileNodesToVertices(diagramFile);
        }
        else
        {
            throw new ArgumentException($"Unsupported file extension `{fileExtension}`. \n" + fileAssociator.GetHelpMessage());
        }
    }

    /// <summary>
    /// Step 1
    /// </summary>
    public void ConvertDrawIoFileNodesToVertices(string filepath)
    {
        drawIoConverter.ProcessFile(filepath);
        ConvertNodesToVertices(drawIoConverter.Roots, drawIoConverter.Edges);
    }

    /// <summary>
    /// Step 1
    /// </summary>
    public void ConvertYedFileNodesToVertices(string filepath)
    {
        YedParser yedParser = new();
        yedParser.Parse(filepath);
        ConvertNodesToVertices(yedParser.GetRootNodes(), yedParser.GetEdges());
    }

    /// <summary>
    /// Step 1
    /// </summary>
    public void ConvertPlantUmlFileNodesToVertices(string filepath)
    {
        var text = File.ReadAllText(filepath);
        ConvertPlantUmlTextNodesToVertices(text);
    }

    /// <summary>
    /// Step 1
    /// </summary>
    public void ConvertPlantUmlTextNodesToVertices(string plantUmlText)
    {
        PlantUMLToNodesEdges translator = new();
        translator.ParseDiagramText(plantUmlText);

        if (translator.HasError())
        {
            string reasons = Input.Antlr4.AntlrError.ErrorsToReasonStrings(translator.GetErrors(), separator: "\n  - ");
            throw new FormatException("PlantUML input failed parsing. Reason(s):\n  - " + reasons);
        }

        ConvertNodesToVertices(new List<DiagramNode> { translator.Root }, translator.Edges);
        FindSingleStateMachine();
    }

    /// <summary>
    /// Step 1. Call this method when you want to support a custom input source.
    /// </summary>
    /// <param name="rootNodes"></param>
    /// <param name="edges"></param>
    public void ConvertNodesToVertices(List<DiagramNode> rootNodes, List<DiagramEdge> edges)
    {
        diagramToSmConverter.CompileDiagramNodesEdges(rootNodes, edges);
    }

    /// <summary>
    /// Step 1. Call this method when you already have created a state machine vertex. Probably from testing.
    /// </summary>
    public void SetStateMachineRoot(StateMachine stateMachine)
    {
        diagramToSmConverter.rootVertices = new List<Vertex>() { stateMachine };
        SetSmVar(stateMachine);
    }

    //------------------------------------------------------------------------

    /// <summary>
    /// Step 2
    /// </summary>
    public void FindStateMachineByName(string stateMachineName)
    {
        var tempSm = new StateMachine("non_null_dummy"); // todo_low: figure out how to not need this to appease nullable analysis
        var action = () => { tempSm = diagramToSmConverter.rootVertices.OfType<StateMachine>().Where(s => s.Name == stateMachineName).Single(); };
        action.RunOrWrapException((e) => new ArgumentException($"Couldn't find state machine in diagram with name `{stateMachineName}`.", e));
        SetSmVar(tempSm);
    }

    /// <summary>
    /// Step 2
    /// </summary>
    [MemberNotNull(nameof(Sm))]
    public void FindSingleStateMachine()
    {
        var tempSm = new StateMachine("non_null_dummy"); // todo_low: figure out how to not need this to appease nullable analysis. Maybe avoid action below.
        var action = () => { tempSm = diagramToSmConverter.rootVertices.OfType<StateMachine>().Single(); };
        action.RunOrWrapException((e) => new ArgumentException($"State machine name not specified. Expected diagram to have find 1 Statemachine node at root level. Instead, found {diagramToSmConverter.rootVertices.OfType<StateMachine>().Count()}.", e));
        SetSmVar(tempSm);
    }

    //------------------------------------------------------------------------

    /// <summary>
    /// Step 3
    /// </summary>
    public void FinishRunning()
    {
        SetupForSingleSm();
        mangler.SetStateMachine(GetStateMachine());
        transformer.RunTransformationPipeline(GetStateMachine());
    }

    [MemberNotNull(nameof(Sm))]
    public void SetupForSingleSm()
    {
        if (Sm == null)
        {
            FindSingleStateMachine();
        }
    }

    [MemberNotNull(nameof(Sm))]
    private void SetSmVar(StateMachine stateMachine)
    {
        Sm = stateMachine;
        stateMachineProvider.SetStateMachine(Sm);
    }

    //------------------------------------------------------------------------

    internal List<Vertex> GetRootVertices()
    {
        return diagramToSmConverter.GetRootVertices();
    }
}
