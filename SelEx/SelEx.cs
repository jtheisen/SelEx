//------------------------------------------------------------------------------
// <copyright file="SelEx.cs" company="Company">
//     Copyright (c) Company.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel.Design;
using System.Globalization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using System.Collections.Generic;
using RoslynSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using System.Linq;
using System.Threading;

namespace SelEx
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class SelEx
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int ExpandCommandId = 0x0100;
        public const int RevertCommandId = 0x0101;
        public const int RegisterKeyBindingsCommandId = 0x0102;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("e504a926-8e47-41e8-ac7a-0ce94185e6d6");

        #region Boilerplate

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly Package package;

        /// <summary>
        /// Initializes a new instance of the <see cref="SelEx"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private SelEx(Package package)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }

            this.package = package;

            OleMenuCommandService commandService = this.ServiceProvider.GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                commandService.AddCommand(new MenuCommand(this.SelExCallback,
                    new CommandID(CommandSet, ExpandCommandId)));

                commandService.AddCommand(new MenuCommand(this.NavigateBackCallback,
                    new CommandID(CommandSet, RevertCommandId)));

                commandService.AddCommand(new MenuCommand(this.RegisterKeyBindingsCallback,
                    new CommandID(CommandSet, RegisterKeyBindingsCommandId)));
            }
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static SelEx Instance {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private IServiceProvider ServiceProvider {
            get {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static void Initialize(Package package)
        {
            Instance = new SelEx(package);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void MenuItemCallback(object sender, EventArgs e)
        {
            string message = string.Format(CultureInfo.CurrentCulture, "Inside {0}.MenuItemCallback()", this.GetType().FullName);
            string title = "SelEx";

            // Show a message box to prove we were here
            VsShellUtilities.ShowMessageBox(
                this.ServiceProvider,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        #endregion

        #region Command bindings

        private void RegisterKeyBindingsCallback(object sender, EventArgs e)
        {
            SetCommandBinding("SelEx.Expand", "Global::Ctrl+Up Arrow");
            SetCommandBinding("SelEx.Revert", "Global::Ctrl+Down Arrow");
        }

        void SetCommandBinding(String commandName, String keyBinding)
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            Command command;
            try
            {
                command = dte.Commands.Item(commandName);
            }
            catch (Exception ex)
            {
                throw new Exception($"Command name {commandName} not found.", ex);
            }
            try
            {
                command.Bindings = keyBinding;
            }
            catch (Exception ex)
            {
                throw new Exception($"Binding expression {keyBinding} not correct.", ex);
            }
        }


        #endregion

        #region Navigation

        void NavigateBackCallback(object sender, EventArgs e)
        {
            GetSelection(out var activeDocument, out var selection, out var espan, out var rspan);

            var top = stack.Peek();

            if (stack.Count > 1 && top.Equals(espan))
            {
                stack.Pop();
                if (stack.Count > 0)
                {
                    var oldEspan = stack.Peek();
                    selection.MoveToAbsoluteOffset(oldEspan.pos1, false);
                    selection.MoveToAbsoluteOffset(oldEspan.pos0, true);
                }
            }
        }

        void SelExCallback(object sender, EventArgs e)
        {
            DoUp();
        }

        async void DoUp()
        {
            GetSelection(out var activeDocument, out var selection, out var espan, out var rspan);

            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var workspace = (Workspace)componentModel.GetService<VisualStudioWorkspace>();

            var documentid = workspace.CurrentSolution.GetDocumentIdsWithFilePath(activeDocument.FullName).FirstOrDefault();
            if (documentid == null) return;

            var document = workspace.CurrentSolution.GetDocument(documentid);

            var textSource = await document.GetTextAsync();

            var text = textSource.ToString();

            var cts = new CancellationTokenSource();

            var root = await document.GetSyntaxRootAsync(cts.Token);

            var node = root.FindNode(rspan, findInsideTrivia: true);

            if (node.Span == rspan)
            {
                node = node.Parent;
            }

            if (node == null) return;

            if (espan.pos0 == espan.pos1)
            {
                stack.Clear();
                stack.Push(espan);
            }

            selection.MoveToAbsoluteOffset(GetAbsolutePositionFromRoslynPosition(text, node.Span.End), false);
            selection.MoveToAbsoluteOffset(GetAbsolutePositionFromRoslynPosition(text, node.SpanStart), true);

            stack.Push(new EditorSpan(selection));
        }

        #endregion

        #region Common

        Int32 GetRoslyPositionFromPoint(VirtualPoint point)
        {
            var pos = point.AbsoluteCharOffset;
            var line = point.Line;
            var offset = point.LineCharOffset - 1;
            var eol = point.AtEndOfLine;
            return pos - 2 + line;
        }

        RoslynSpan GetRoslynSpanFromPoints(VirtualPoint point0, VirtualPoint point1)
        {
            var pos0 = GetRoslyPositionFromPoint(point0);
            var pos1 = GetRoslyPositionFromPoint(point1);
            return new RoslynSpan(Math.Min(pos0, pos1) - (pos0 == pos1 ? 1 : 0), Math.Abs(pos1 - pos0));
        }

        Int32 GetAbsolutePositionFromRoslynPosition(String text, Int32 roslynPosition)
        {
            var stext = text.Substring(0, roslynPosition);
            var lines = stext.Count(c => c == '\r');
            return roslynPosition - lines + 1;
        }

        Int32 GetAbsoluteDistance(String dist)
        {
            return dist.Replace("\r", "").Length;
        }


        struct EditorSpan
        {
            public Int32 pos0;
            public Int32 pos1;

            public EditorSpan(TextSelection selection)
            {
                var p0 = selection.AnchorPoint.AbsoluteCharOffset;
                var p1 = selection.ActivePoint.AbsoluteCharOffset;
                var i = selection.ActivePoint.LineCharOffset;
                pos0 = Math.Min(p0, p1);
                pos1 = Math.Max(p0, p1);
            }
        }

        void GetSelection(out EnvDTE.Document activeDocument, out TextSelection selection, out EditorSpan espan, out RoslynSpan rspan)
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            activeDocument = dte?.ActiveDocument;
            if (activeDocument == null) throw new WrongContextException();
            selection = (TextSelection)activeDocument.Selection;
            espan = new EditorSpan(selection);
            var rpos = GetRoslyPositionFromPoint(selection.ActivePoint);
            rspan = GetRoslynSpanFromPoints(selection.AnchorPoint, selection.ActivePoint);
        }

        Stack<EditorSpan> stack = new Stack<EditorSpan>();

        #endregion
    }

    [Serializable]
    internal class WrongContextException : Exception
    {
    }
}
