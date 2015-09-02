' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.ComponentModel.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.Editor.Commands
Imports Microsoft.CodeAnalysis.Editor.Host
Imports Microsoft.CodeAnalysis.Editor.Shared.Utilities
Imports Microsoft.CodeAnalysis.Formatting.Rules
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.Text.Shared.Extensions
Imports Microsoft.VisualStudio.Text
Imports Microsoft.VisualStudio.Text.Editor
Imports Microsoft.VisualStudio.Text.Operations
Imports Microsoft.VisualStudio.Utilities

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.LineCommit
    ''' <summary>
    ''' Watches for the enter key being pressed, and triggers a commit in response.
    ''' </summary>
    ''' <remarks>This particular command filter acts as a "wrapper" around any other command, as it
    ''' wishes to invoke the commit after whatever processed the enter is done doing what it's
    ''' doing.</remarks>
    <ExportCommandHandler(PredefinedCommandHandlerNames.Commit, ContentTypeNames.VisualBasicContentType)>
    <Order(Before:=PredefinedCommandHandlerNames.EndConstruct)>
    <Order(Before:=PredefinedCommandHandlerNames.Completion)>
    Friend Class CommitCommandHandler
        Implements ICommandHandler(Of ReturnKeyCommandArgs)
        Implements ICommandHandler(Of PasteCommandArgs)
        Implements ICommandHandler(Of SaveCommandArgs)
        Implements ICommandHandler(Of FormatDocumentCommandArgs)
        Implements ICommandHandler(Of FormatSelectionCommandArgs)

        Private ReadOnly _bufferManagerFactory As CommitBufferManagerFactory
        Private ReadOnly _editorOperationsFactoryService As IEditorOperationsFactoryService
        Private ReadOnly _smartIndentationService As ISmartIndentationService
        Private ReadOnly _textUndoHistoryRegistry As ITextUndoHistoryRegistry
        Private ReadOnly _waitIndicator As IWaitIndicator

        <ImportingConstructor()>
        Friend Sub New(
            bufferManagerFactory As CommitBufferManagerFactory,
            editorOperationsFactoryService As IEditorOperationsFactoryService,
            smartIndentationService As ISmartIndentationService,
            textUndoHistoryRegistry As ITextUndoHistoryRegistry,
            waitIndicator As IWaitIndicator)

            _bufferManagerFactory = bufferManagerFactory
            _editorOperationsFactoryService = editorOperationsFactoryService
            _smartIndentationService = smartIndentationService
            _textUndoHistoryRegistry = textUndoHistoryRegistry
            _waitIndicator = waitIndicator
        End Sub

        Public Sub ExecuteCommand(args As FormatDocumentCommandArgs, nextHandler As Action) Implements ICommandHandler(Of FormatDocumentCommandArgs).ExecuteCommand
            If Not args.SubjectBuffer.CanApplyChangeDocumentToWorkspace() Then
                nextHandler()
                Return
            End If

            _waitIndicator.Wait(
                VBEditorResources.FormatDocument,
                VBEditorResources.FormattingDocument,
                allowCancel:=True,
                action:=
                Sub(waitContext)
                    Dim buffer = args.SubjectBuffer
                    Dim snapshot = buffer.CurrentSnapshot

                    Dim wholeFile = snapshot.GetFullSpan()
                    Dim commitBufferManager = _bufferManagerFactory.CreateForBuffer(buffer)
                    commitBufferManager.ExpandDirtyRegion(wholeFile)
                    commitBufferManager.CommitDirty(isExplicitFormat:=True, cancellationToken:=waitContext.CancellationToken)
                End Sub)
        End Sub

        Public Function GetCommandState(args As FormatDocumentCommandArgs, nextHandler As Func(Of CommandState)) As CommandState Implements ICommandHandler(Of FormatDocumentCommandArgs).GetCommandState
            Return nextHandler()
        End Function

        Public Sub ExecuteCommand(args As FormatSelectionCommandArgs, nextHandler As Action) Implements ICommandHandler(Of FormatSelectionCommandArgs).ExecuteCommand
            If Not args.SubjectBuffer.CanApplyChangeDocumentToWorkspace() Then
                nextHandler()
                Return
            End If

            _waitIndicator.Wait(
                VBEditorResources.FormatDocument,
                VBEditorResources.FormattingDocument,
                allowCancel:=True,
                action:=
                Sub(waitContext)
                    Dim buffer = args.SubjectBuffer
                    Dim selections = args.TextView.Selection.GetSnapshotSpansOnBuffer(buffer)

                    If selections.Count < 1 Then
                        nextHandler()
                        Return
                    End If

                    Dim snapshot = buffer.CurrentSnapshot
                    For index As Integer = 0 To selections.Count - 1
                        Dim textspan = CommonFormattingHelpers.GetFormattingSpan(snapshot, selections(0))
                        Dim selectedSpan = New SnapshotSpan(snapshot, textspan.Start, textspan.Length)
                        Dim commitBufferManager = _bufferManagerFactory.CreateForBuffer(buffer)
                        commitBufferManager.ExpandDirtyRegion(selectedSpan)
                        commitBufferManager.CommitDirty(isExplicitFormat:=True, cancellationToken:=waitContext.CancellationToken)
                    Next
                End Sub)

            'We don't call nextHandler, since we have handled this command.
        End Sub

        Public Function GetCommandState(args As FormatSelectionCommandArgs, nextHandler As Func(Of CommandState)) As CommandState Implements ICommandHandler(Of FormatSelectionCommandArgs).GetCommandState
            Return nextHandler()
        End Function

        Public Sub ExecuteCommand(args As ReturnKeyCommandArgs, nextHandler As Action) Implements ICommandHandler(Of ReturnKeyCommandArgs).ExecuteCommand
            If Not args.SubjectBuffer.GetOption(FeatureOnOffOptions.PrettyListing) Then
                nextHandler()
                Return
            End If

            ' Commit is not cancellable.
            Dim _cancellationToken = CancellationToken.None
            Dim bufferManager = _bufferManagerFactory.CreateForBuffer(args.SubjectBuffer)
            Dim oldCaretPoint = args.TextView.GetCaretPoint(args.SubjectBuffer)

            ' When we call nextHandler(), it's possible that some other feature might try to move the caret
            ' or something similar. We want this "outer" commit to be the real one, so start suppressing any
            ' re-entrant commits.
            Dim suppressionHandle = bufferManager.BeginSuppressingCommits()

            Try
                ' Evil: we really want a enter in VB to be always grouped as a single undo transaction, and so make sure all
                ' things from here on out are grouped as one.
                Using transaction = _textUndoHistoryRegistry.GetHistory(args.TextView.TextBuffer).CreateTransaction(VBEditorResources.InsertNewLine)
                    transaction.MergePolicy = AutomaticCodeChangeMergePolicy.Instance

                    nextHandler()

                    If ShouldCommitFromEnter(oldCaretPoint, args.TextView, args.SubjectBuffer, _cancellationToken) Then
                        ' We are done suppressing at this point
                        suppressionHandle.Dispose()
                        suppressionHandle = Nothing

                        bufferManager.CommitDirty(isExplicitFormat:=False, cancellationToken:=_cancellationToken)

                        ' We may have reindented the surrounding block, so let's recompute
                        ' where we should end up
                        Dim newCaretPosition = args.TextView.GetCaretPoint(args.SubjectBuffer)

                        If newCaretPosition.HasValue Then
                            Dim currentLine = newCaretPosition.Value.GetContainingLine()
                            Dim desiredIndentation = args.TextView.GetDesiredIndentation(_smartIndentationService, currentLine)
                            If currentLine.Length = 0 AndAlso desiredIndentation.HasValue Then
                                args.TextView.TryMoveCaretToAndEnsureVisible(New VirtualSnapshotPoint(currentLine.Start, desiredIndentation.Value))
                                _editorOperationsFactoryService.GetEditorOperations(args.TextView).AddAfterTextBufferChangePrimitive()
                            End If
                        End If
                    End If

                    transaction.Complete()
                End Using
            Finally
                ' Resume committing
                If suppressionHandle IsNot Nothing Then
                    suppressionHandle.Dispose()
                End If
            End Try
        End Sub

        Private Function ShouldCommitFromEnter(
            oldCaretPositionInOldSnapshot As SnapshotPoint?,
            textView As ITextView,
            subjectBuffer As ITextBuffer,
            cancellationToken As CancellationToken) As Boolean

            Dim newCaretPosition = textView.GetCaretPoint(subjectBuffer)

            If Not oldCaretPositionInOldSnapshot.HasValue OrElse Not newCaretPosition.HasValue Then
                ' We somehow moved in or out of our subject buffer during this commit. Not sure how,
                ' but that counts as losing focus and thus is a commit
                Return True
            End If

            ' Map oldCaretPosition forward to the current snapshot
            Dim oldCaretPositionInCurrentSnapshot = oldCaretPositionInOldSnapshot.Value.CreateTrackingPoint(PointTrackingMode.Negative).GetPoint(subjectBuffer.CurrentSnapshot)
            Dim bufferManager = _bufferManagerFactory.CreateForBuffer(subjectBuffer)

            If bufferManager.IsMovementBetweenStatements(oldCaretPositionInCurrentSnapshot, newCaretPosition.Value, cancellationToken) Then
                Dim document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges()
                If document Is Nothing Then
                    Return False
                End If

                Dim tree = document.GetSyntaxTreeAsync(cancellationToken).WaitAndGetResult(cancellationToken)
                Dim token = tree.FindTokenOnLeftOfPosition(oldCaretPositionInOldSnapshot.Value, cancellationToken)

                If token.IsKind(SyntaxKind.StringLiteralToken) AndAlso token.FullSpan.Contains(oldCaretPositionInOldSnapshot.Value) Then
                    Return False
                End If

                ' Get the statement which we pressed enter for
                Dim oldStatement = ContainingStatementInfo.GetInfo(oldCaretPositionInCurrentSnapshot, tree, cancellationToken)

                Return oldStatement Is Nothing OrElse Not oldStatement.IsIncomplete
            End If

            Return False
        End Function

        Public Function GetCommandState(args As ReturnKeyCommandArgs, nextHandler As Func(Of CommandState)) As CommandState Implements ICommandHandler(Of ReturnKeyCommandArgs).GetCommandState
            ' We don't make any decision if the enter key is allowed; we just forward onto the next handler
            Return nextHandler()
        End Function

        Public Sub ExecuteCommand(args As PasteCommandArgs, nextHandler As Action) Implements ICommandHandler(Of PasteCommandArgs).ExecuteCommand
            _waitIndicator.Wait(
                title:=VBEditorResources.FormatPaste,
                message:=VBEditorResources.FormattingPastedText,
                allowCancel:=True,
                action:=Sub(waitContext) CommitOnPaste(args, nextHandler, waitContext))
        End Sub

        Private Sub CommitOnPaste(args As PasteCommandArgs, nextHandler As Action, waitContext As IWaitContext)
            Using transaction = _textUndoHistoryRegistry.GetHistory(args.TextView.TextBuffer).CreateTransaction(VBEditorResources.Paste)
                Dim oldVersion = args.SubjectBuffer.CurrentSnapshot.Version

                ' Do the paste in the same transaction as the commit/format
                nextHandler()

                If Not args.SubjectBuffer.GetOption(FeatureOnOffOptions.FormatOnPaste) Then
                    transaction.Complete()
                    Return
                End If

                Dim document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges()
                If document IsNot Nothing Then
                    Dim formattingRuleService = document.Project.Solution.Workspace.Services.GetService(Of IHostDependentFormattingRuleFactoryService)()
                    If formattingRuleService.ShouldNotFormatOrCommitOnPaste(document) Then
                        transaction.Complete()
                        Return
                    End If
                End If

                ' Did we paste content that changed the number of lines?
                If oldVersion.Changes IsNot Nothing AndAlso oldVersion.Changes.IncludesLineChanges Then
                    Try
                        _bufferManagerFactory.CreateForBuffer(args.SubjectBuffer).CommitDirty(isExplicitFormat:=False, cancellationToken:=waitContext.CancellationToken)
                    Catch ex As OperationCanceledException
                        ' If the commit was cancelled, we still want the paste to go through
                    End Try
                End If

                transaction.Complete()
            End Using
        End Sub

        Public Function GetCommandState(args As PasteCommandArgs, nextHandler As Func(Of CommandState)) As CommandState Implements ICommandHandler(Of PasteCommandArgs).GetCommandState
            Return nextHandler()
        End Function

        Public Sub ExecuteCommand(args As SaveCommandArgs, nextHandler As Action) Implements ICommandHandler(Of SaveCommandArgs).ExecuteCommand
            If args.SubjectBuffer.GetOption(InternalFeatureOnOffOptions.FormatOnSave) Then
                _waitIndicator.Wait(
                    title:=VBEditorResources.FormatOnSave,
                    message:=VBEditorResources.FormattingDocument,
                    allowCancel:=True,
                    action:=Sub(waitContext)
                                Using transaction = _textUndoHistoryRegistry.GetHistory(args.TextView.TextBuffer).CreateTransaction(VBEditorResources.FormatOnSave)
                                    _bufferManagerFactory.CreateForBuffer(args.SubjectBuffer).CommitDirty(isExplicitFormat:=False, cancellationToken:=waitContext.CancellationToken)

                                    ' We should only create the transaction if anything actually happened
                                    If transaction.UndoPrimitives.Any() Then
                                        transaction.Complete()
                                    End If
                                End Using
                            End Sub)
            End If

            nextHandler()
        End Sub

        Public Function GetCommandState(args As SaveCommandArgs, nextHandler As Func(Of CommandState)) As CommandState Implements ICommandHandler(Of SaveCommandArgs).GetCommandState
            Return nextHandler()
        End Function
    End Class
End Namespace
