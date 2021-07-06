﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CTA.WebForms2Blazor.Extensions;
using CTA.WebForms2Blazor.FileInformationModel;
using CTA.WebForms2Blazor.Helpers;
using CTA.WebForms2Blazor.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CTA.WebForms2Blazor.ClassConverters
{
    public class GlobalClassConverter : ClassConverter
    {
        // These are special events that are fired only a couple of times independently
        // of request pipeline events, they need to be handled specially
        private const string ApplicationStartMethodName = "Application_Start";
        private const string ApplicationEndMethodName = "Application_End";
        private const string SessionStartMethodName = "Session_Start";
        private const string SessionEndMethodName = "Session_End";

        private const string AddMiddlewareUsingsOperation = "retrieve and add usings for middleware namespaces";
        private const string ConfigureRequestPipelineOperation = "configure request pipeline";
        private const string MigrateServiceLayerOperation = "migrate service layer and configure depenency injection in ConfigureServices()";

        private LifecycleManagerService _lifecycleManager;

        private IEnumerable<StatementSyntax> _configureMethodStatements;
        private IEnumerable<MethodDeclarationSyntax> _keepableMethods;
        private IEnumerable<string> _endOfClassComments;

        public GlobalClassConverter(
            string relativePath,
            string sourceProjectPath,
            SemanticModel sourceFileSemanticModel,
            TypeDeclarationSyntax originalDeclarationSyntax,
            INamedTypeSymbol originalClassSymbol,
            LifecycleManagerService lifecycleManager,
            TaskManagerService taskManager)
            : base(relativePath, sourceProjectPath, sourceFileSemanticModel, originalDeclarationSyntax, originalClassSymbol, taskManager)
        {
            _lifecycleManager = lifecycleManager;
            _lifecycleManager.NotifyExpectedMiddlewareSource();

            _configureMethodStatements = new List<StatementSyntax>();
            _keepableMethods = new List<MethodDeclarationSyntax>();
            _endOfClassComments = new List<string>();
        }

        public override async Task<FileInformation> MigrateClassAsync()
        {
            // Make this call once now so we don't have to keep doing it later
            var originalDescendantNodes = _originalDeclarationSyntax.DescendantNodes();

            // Currently not implementing service layer so add blank line with a comment instead
            var configureServicesLines = new[]
            {
                CodeSyntaxHelper.GetBlankLine().AddComment(string.Format(Constants.OperationUnattemptedCommentTemplate, MigrateServiceLayerOperation), isLeading: false)
            }; 

            // Iterate through methods and process them as needed
            ProcessMethods(originalDescendantNodes.OfType<MethodDeclarationSyntax>());

            // With middleware lambdas added we need to retrieve registrations
            // before we have all statements required to build startup class
            await InsertRequestPipelineMiddlewareRegistrations();

            var startupClassDeclaration = StartupSyntaxHelper.ConstructStartupClass(
                constructorAdditionalStatements: originalDescendantNodes.OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Body?.Statements,
                configureAdditionalStatements: _configureMethodStatements,
                configureServicesAdditionalStatements: configureServicesLines,
                additionalFieldDeclarations: originalDescendantNodes.OfType<FieldDeclarationSyntax>(),
                additionalPropertyDeclarations: originalDescendantNodes.OfType<PropertyDeclarationSyntax>(),
                additionalMethodDeclarations: _keepableMethods)
                .AddClassBlockComment(_endOfClassComments, false);

            var containingNamespace = CodeSyntaxHelper.BuildNamespace(_originalClassSymbol.ContainingNamespace.Name, startupClassDeclaration);
            var fileText = CodeSyntaxHelper.GetFileSyntaxAsString(containingNamespace, await GetAllUsingStatements());

            DoCleanUp();

            // Global.asax.cs turns into Startup.cs
            return new FileInformation(Path.Combine(Path.GetDirectoryName(_relativePath), Constants.StartupFileName), Encoding.UTF8.GetBytes(fileText));
        }

        private void ProcessMethods(IEnumerable<MethodDeclarationSyntax> orignalMethods)
        {
            foreach (var method in orignalMethods)
            {
                var lifecycleEvent = LifecycleManagerService.CheckMethodApplicationLifecycleHook(method);

                if (lifecycleEvent != null)
                {
                    HandleLifecycleMethod(method, (WebFormsAppLifecycleEvent)lifecycleEvent);
                }
                else if (method.IsEventHandler(ApplicationStartMethodName))
                {
                    var newStatements = method.Body.Statements
                        // Make a note of where these lines came from
                        .AddComment(string.Format(Constants.CodeOriginCommentTemplate, ApplicationStartMethodName))
                        // Add blank line before new statements to give some separation from previous statements
                        .Prepend(CodeSyntaxHelper.GetBlankLine());

                    _configureMethodStatements = _configureMethodStatements.Concat(newStatements);
                }
                else if (method.IsEventHandler(ApplicationEndMethodName) || method.IsEventHandler(SessionStartMethodName) || method.IsEventHandler(SessionEndMethodName))
                {
                    CommentOutMethod(method);
                }
                else
                {
                    _keepableMethods = _keepableMethods.Append(method);
                }
            }

            // We added all discovered middleware methods as lambdas so global has been
            // processed as a middleware source
            _lifecycleManager.NotifyMiddlewareSourceProcessed();
        }

        private async Task<IEnumerable<UsingDirectiveSyntax>> GetAllUsingStatements()
        {
            var typeRequiredNamespaceNames = _sourceFileSemanticModel
                .GetNamespacesReferencedByType(_originalDeclarationSyntax)
                .Select(namespaceSymbol => namespaceSymbol.ToDisplayString());

            // Merging required using statements for middleware and source type contents
            try
            {
                var middlewareNamespaceNames = await _taskManager.ManagedRun(_taskId, (token) => _lifecycleManager.GetMiddlewareNamespaces(token));
                return CodeSyntaxHelper.BuildUsingStatements(typeRequiredNamespaceNames.Union(middlewareNamespaceNames));
            }
            catch (OperationCanceledException e)
            {
                // TODO: Log this failure

                return CodeSyntaxHelper.BuildUsingStatements(typeRequiredNamespaceNames)
                    .AddComment(string.Format(Constants.OperationFailedCommentTemplate, AddMiddlewareUsingsOperation));
            }
        }

        private void HandleLifecycleMethod(MethodDeclarationSyntax methodDeclaration, WebFormsAppLifecycleEvent lifecycleEvent)
        {
            var statements = methodDeclaration.Body.Statements;
            var lambdaExpression = LifecycleManagerService.ContentIsPreHandle(lifecycleEvent) ?
                MiddlewareSyntaxHelper.ConstructMiddlewareLambda(preHandleStatements: statements) :
                MiddlewareSyntaxHelper.ConstructMiddlewareLambda(postHandleStatements: statements);

            _lifecycleManager.RegisterMiddlewareLambda(lifecycleEvent, lambdaExpression);
        }

        private void CommentOutMethod(MethodDeclarationSyntax methodDeclaration)
        {
            // Get the method as a series of strings and give the reason it was removed
            var newComments = methodDeclaration.AsStringsByLine().Prepend(Constants.UnusableCodeComment);

            if (_endOfClassComments.Any())
            {
                // Prepend an extra space to get separation from last commented method
                newComments = newComments.Prepend(string.Empty);
            }

            _endOfClassComments = _endOfClassComments.Concat(newComments);
        }

        private async Task InsertRequestPipelineMiddlewareRegistrations()
        {
            try
            {
                var pipelineAdditions = await _taskManager.ManagedRun(_taskId, (token) => _lifecycleManager.GetMiddlewarePipelineAdditions(token));

                _configureMethodStatements = _configureMethodStatements.Concat(pipelineAdditions);
            }
            catch (OperationCanceledException e)
            {
                // TODO: Log this failure

                var failureComment = string.Format(Constants.OperationFailedCommentTemplate, ConfigureRequestPipelineOperation);

                if (!_configureMethodStatements.Any())
                {
                    // If we don't have any extra statements for configure() add a blank
                    // one to attach our comment to
                    _configureMethodStatements = _configureMethodStatements.Append(CodeSyntaxHelper.GetBlankLine().AddComment(failureComment, isLeading: false));
                }
                else
                {
                    _configureMethodStatements = _configureMethodStatements.AddComment(failureComment, isLeading: false);
                }
            }
        }
    }
}