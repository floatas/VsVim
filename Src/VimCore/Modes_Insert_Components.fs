﻿#light

namespace Vim.Modes.Insert
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Utilities
open Vim

/// Used to track and accumulate the changes to ITextView instance
type internal ITextChangeTracker =

    /// Associated ITextView
    abstract TextView: ITextView

    /// Whether or not change tracking is currently enabled.  Disabling the tracking will
    /// cause the current change to be completed
    abstract TrackCurrentChange: bool with get, set

    /// Whether we should suppress updating last change marks
    abstract SuppressLastChangeMarks: bool with get, set

    /// Current change
    abstract CurrentChange: TextChange option

    /// Complete the current change if there is one
    abstract CompleteChange: unit -> unit

    /// Clear out the current change without completing it
    abstract ClearChange: unit -> unit

    /// Start tracking the effective change
    abstract StartTrackingEffectiveChange: unit -> unit

    /// Whether the effective change is a simple insert
    abstract IsEffectiveChangeInsert: bool

    /// A span representing the active insert region of the effective change
    abstract EffectiveChange: SnapshotSpan option

    /// Stop tracking the effective change
    abstract StopTrackingEffectiveChange: unit -> unit

    /// Raised when a change is completed
    [<CLIEvent>]
    abstract ChangeCompleted: IDelegateEvent<System.EventHandler<TextChangeEventArgs>>

/// Data relating to an effective change
type EffectiveChangeData = {

    // The current caret position
    CaretPosition: CaretPosition

    /// The left edge of the active insert region
    LeftEdge: int

    /// The right edge of the active insert region
    RightEdge: int

    /// The total number of characters deleted to the left of the active insert region
    LeftDeletions: int

    /// The total number of character deleted to the right of the active insert region
    RightDeletions: int
}

/// Used to track changes to an individual IVimBuffer
type internal TextChangeTracker
    ( 
        _vimTextBuffer: IVimTextBuffer,
        _textView: ITextView,
        _operations: ICommonOperations
    ) as this =

    static let Key = System.Object()

    let _bag = DisposableBag()
    let _changeCompletedEvent = StandardEvent<TextChangeEventArgs>()

    /// Tracks the current active text change.  This will grow as the user edits
    let mutable _currentTextChange: (TextChange * ITextChange) option = None

    /// Whether or not tracking is currently enabled
    let mutable _trackCurrentChange = false

    /// Whether we should suppress updating last change marks.
    /// Insert mode turns this off and manages the marks itself
    let mutable _suppressLastChangeMarks = false

    let mutable (_effectiveChangeData: EffectiveChangeData option) = None

    do
        // Listen to text buffer change events in order to track edits.  Don't respond to changes
        // while disabled though
        _textView.TextBuffer.Changed
        |> Observable.subscribe (fun args -> this.OnTextChanged args)
        |> _bag.Add

        // Listen to caret position changed events.
        _textView.Caret.PositionChanged
        |> Observable.subscribe (fun args -> this.OnPositionChanged args)
        |> _bag.Add

        // Dispose handlers when the text view is closed.
        _textView.Closed 
        |> Event.add (fun _ -> _bag.DisposeAll())

    member x.CurrentChange = 
        match _currentTextChange with
        | None -> None
        | Some (change,_) -> Some change

    member x.TrackCurrentChange
        with get () = _trackCurrentChange
        and set value = 
            if _trackCurrentChange <> value then
                _currentTextChange <- None
                _trackCurrentChange <- value

    /// The change is completed.  Raises the changed event and resets the current text change 
    /// state
    member x.CompleteChange() = 
        match _currentTextChange with
        | None -> 
            ()
        | Some (change, _) -> 
            _currentTextChange <- None
            let args = TextChangeEventArgs(change)
            _changeCompletedEvent.Trigger x args

    /// Clear out the current change without completing it
    member x.ClearChange() =
        _currentTextChange <- None

    /// Start tracking the effective change
    member x.StartTrackingEffectiveChange() =
        let caretPosition = _textView.Caret.Position
        let position = caretPosition.BufferPosition.Position
        _effectiveChangeData <-
            Some {
                CaretPosition = caretPosition;
                LeftEdge = position;
                RightEdge = position;
                LeftDeletions = 0;
                RightDeletions = 0;
            }

    /// Whether the effective change is a simple insert
    member x.IsEffectiveChangeInsert =
        match _effectiveChangeData with
        | Some data ->
            data.LeftDeletions = 0 && data.RightDeletions = 0
        | None ->
            false

    /// A span representing the active insert region of the effective change
    member x.EffectiveChange =
        match _effectiveChangeData with
        | Some data ->
            SnapshotSpan(_textView.TextSnapshot, data.LeftEdge, data.RightEdge - data.LeftEdge)
            |> Some
        | None ->
            None

    /// Stop tracking the effective change
    member x.StopTrackingEffectiveChange() =
        _effectiveChangeData <- None

    /// Convert the ITextChange value into a TextChange instance.  This will not handle any special 
    /// edit patterns and simply does a raw adjustment
    member x.ConvertBufferChange (beforeSnapshot: ITextSnapshot) (change: ITextChange) =

        let convert () = 

            let getDelete () = 
                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let isCaretAtStart = 
                    caretPoint.Snapshot = beforeSnapshot &&
                    caretPoint.Position = change.OldPosition

                if isCaretAtStart then
                    TextChange.DeleteRight change.OldLength
                else
                    TextChange.DeleteLeft change.OldLength

            if change.OldText.Length = 0 then
                // This is a straight insert operation
                TextChange.Insert change.NewText
            elif change.NewText.Length = 0 then 
                // This is a straight delete operation
                getDelete()
            else
                // This is a delete + insert combination 
                let left = getDelete()
                let right = TextChange.Insert change.NewText
                TextChange.Combination (left, right)

        // Look for the pattern where tabs are used to replace spaces.  When there are spaces in 
        // the ITextBuffer and tabs are enabled and the user hits <Tab> the spaces will be deleted
        // and replaced with tabs.  The result of the edit though should be recorded as simply 
        // tabs
        if change.OldText.Length > 0 && StringUtil.IsBlanks change.NewText && StringUtil.IsBlanks change.OldText then
            let oldText = _operations.NormalizeBlanks change.OldText
            let newText = _operations.NormalizeBlanks change.NewText
            if newText.StartsWith oldText then
                let diffText = newText.Substring(oldText.Length)
                TextChange.Insert diffText 
            else
                convert ()
        else 
            convert ()

    member x.OnTextChanged (args: TextContentChangedEventArgs) = 

        if x.TrackCurrentChange then
            x.UpdateCurrentChange args

        if _effectiveChangeData.IsSome then
            x.UpdateEffectiveChange args

        x.UpdateMarks args

    member x.OnPositionChanged (args: CaretPositionChangedEventArgs) =
        VimTrace.TraceInfo("OnCaretPositionChanged: old = {0}, new = {1}", args.OldPosition, args.NewPosition)
        match _effectiveChangeData with
        | Some data ->
            _effectiveChangeData <- Some { data with CaretPosition = args.NewPosition }
        | None ->
            ()

    member x.UpdateCurrentChange (args: TextContentChangedEventArgs) = 

        // At this time we only support contiguous changes (or rather a single change)
        if args.Changes.Count = 1 then
            let newBufferChange = args.Changes.Item(0)
            let newTextChange = x.ConvertBufferChange args.Before newBufferChange

            match _currentTextChange with
            | None -> _currentTextChange <- Some (newTextChange, newBufferChange)
            | Some (oldTextChange, oldBufferChange) -> x.MergeChange oldTextChange oldBufferChange newTextChange newBufferChange 
        else
            x.CompleteChange()

    /// Update the last edit point and last change marks based on the latest change to the
    /// ITextBuffer.  Note that this isn't necessarily a vim originated edit.  Can be done
    /// by another Visual Studio operation but we still treat it like a Vim edit.
    member x.UpdateMarks (args: TextContentChangedEventArgs) = 

        if args.Changes.Count = 1 then
            let change = args.Changes.Item(0)
            let position = 
                if change.Delta > 0 && change.NewEnd > 0 then
                    change.NewEnd - 1
                else
                    change.NewPosition
            let point = SnapshotPoint(args.After, position)
            _vimTextBuffer.LastEditPoint <- Some point

            // If we not suppressing change marks, automatically update the
            // last change start and end positions.
            if not _suppressLastChangeMarks then
                let oldSpan = SnapshotSpan(args.Before, change.OldSpan)
                let newSpan = SnapshotSpan(args.After, change.NewSpan)
                _operations.RecordLastChange oldSpan newSpan
        else
            // When there are multiple changes it is usually the result of a projection 
            // buffer edit coming from a web page edit.  For now that's unsupported
            _vimTextBuffer.LastEditPoint <- None

    /// Attempt to merge the change operations together
    member x.MergeChange oldTextChange (oldChange: ITextChange) newTextChange (newChange: ITextChange) =

        // First step is to determine if we can merge the changes.  Essentially we need to ensure that
        // the changes are occurring at the same point in the ITextBuffer 
        let canMerge = 
            if newChange.OldText.Length = 0 then 
                // This is a pure insert so it must occur at the end of the last ITextChange
                // in order to be valid 
                oldChange.NewEnd = newChange.OldPosition
            elif newChange.NewText.Length = 0 then 
                // This is a pure delete operation.  The delete must start from the end of the 
                // previous change 
                oldChange.NewEnd = newChange.OldEnd
            elif newChange.Delta >= 0 && oldChange.NewEnd = newChange.OldPosition then
                // Replace just after the previous edit
                true
            else
                // This is a replace operation.  Easiest way to check is to see if the delta applied
                // to the previous end puts us in the correct end position
                oldChange.NewEnd - newChange.OldText.Length = newChange.OldPosition

        if canMerge then 
            // Change is legal.  Merge it with the existing one and move on
            let mergedChange = TextChange.CreateReduced oldTextChange newTextChange 
            _currentTextChange <- Some (mergedChange, newChange)
        else
            // If we can't merge then the previous change is complete and we can switch our focus to 
            // the new TextChange value
            x.CompleteChange()
            _currentTextChange <- Some (newTextChange, newChange)

    member x.UpdateEffectiveChange (args: TextContentChangedEventArgs) =

        VimTrace.TraceInfo("OnTextChange: {0}", args.Changes.Count)
        for i = 0 to args.Changes.Count - 1 do
            VimTrace.TraceInfo("OnTextChange: change {0}", i)
            let change = args.Changes.[i]
            VimTrace.TraceInfo("OnTextChange: old = '{0}', new = '{1}'", change.OldText, change.NewText)
            VimTrace.TraceInfo("OnTextChange: old = '{0}', new = '{1}'", change.OldSpan, change.NewSpan)
            VimTrace.TraceInfo("OnTextChange: caret position = {0}", _textView.Caret.Position.BufferPosition)

        match args.Changes.Count, _effectiveChangeData with
        | 1, Some data ->
            let mutable leftEdge = data.LeftEdge
            let mutable rightEdge = data.RightEdge
            let mutable leftDeletions = data.LeftDeletions
            let mutable rightDeletions = data.RightDeletions

            let change = args.Changes.[0]
            let virtualSpaces = data.CaretPosition.VirtualSpaces
            if
                virtualSpaces > 0
                && change.OldSpan.Start = leftEdge
                && change.OldSpan.End = rightEdge
                && change.NewSpan.Start = leftEdge
                && change.NewText.Length >= virtualSpaces
                && change.NewText.Substring(0, virtualSpaces) |> StringUtil.IsBlanks
            then

                // Caret was moved from virtual space to non-virtual space.
                leftEdge <- leftEdge + virtualSpaces
                rightEdge <- change.NewSpan.End

            elif change.OldSpan.End < leftEdge then

                // Change entirely precedes the active region so shift the edges by the delta.
                leftEdge <- leftEdge + change.Delta
                rightEdge <- rightEdge + change.Delta

            elif change.OldSpan.Start > rightEdge then

                // Change entirely follows the active region, so we can ignore it.
                ()

            elif change.OldSpan.Start >= leftEdge && change.OldSpan.End <= rightEdge then

                // Change falls completely within the active region so shift the right edge.
                rightEdge <- rightEdge + change.Delta

            else

                // Change is neither a subsest nor disjoint with
                // the active region. Handle overlap.

                // Check for deletions to the left.
                if change.OldSpan.Start < leftEdge then
                    let deleted = leftEdge - change.OldSpan.Start
                    leftEdge <- change.NewSpan.Start
                    leftDeletions <- leftDeletions + deleted

                // Check for deletions to the right.
                if change.OldSpan.End > rightEdge then
                    let deleted = change.OldSpan.End - rightEdge
                    rightEdge <- change.NewSpan.End
                    rightDeletions <- rightDeletions + deleted
                else
                    rightEdge <- rightEdge + change.Delta

            _effectiveChangeData <-
                Some {
                    CaretPosition = _textView.Caret.Position;
                    LeftEdge = leftEdge;
                    RightEdge = rightEdge;
                    LeftDeletions = leftDeletions;
                    RightDeletions = rightDeletions;
                }

        | _ ->
            ()

    static member GetTextChangeTracker (bufferData: IVimBufferData) (commonOperationsFactory: ICommonOperationsFactory) =
        let textView = bufferData.TextView
        textView.Properties.GetOrCreateSingletonProperty(Key, (fun () -> 
            let operations = commonOperationsFactory.GetCommonOperations bufferData
            TextChangeTracker(bufferData.VimTextBuffer, textView, operations)))

    interface ITextChangeTracker with 
        member x.TextView = _textView
        member x.TrackCurrentChange
            with get () = x.TrackCurrentChange
            and set value = x.TrackCurrentChange <- value
        member x.SuppressLastChangeMarks
            with get () = _suppressLastChangeMarks
            and set value = _suppressLastChangeMarks <- value
        member x.CurrentChange = x.CurrentChange
        member x.CompleteChange () = x.CompleteChange ()
        member x.ClearChange () = x.ClearChange ()
        member x.StartTrackingEffectiveChange () = x.StartTrackingEffectiveChange ()
        member x.IsEffectiveChangeInsert = x.IsEffectiveChangeInsert
        member x.EffectiveChange = x.EffectiveChange
        member x.StopTrackingEffectiveChange () = x.StopTrackingEffectiveChange ()
        [<CLIEvent>]
        member x.ChangeCompleted = _changeCompletedEvent.Publish
