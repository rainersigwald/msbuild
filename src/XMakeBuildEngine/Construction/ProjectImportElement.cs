﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// </copyright>
// <summary>Definition of ProjectImportElement class.</summary>
//-----------------------------------------------------------------------

using System.Diagnostics;

using Microsoft.Build.Shared;

using ProjectXmlUtilities = Microsoft.Build.Internal.ProjectXmlUtilities;

namespace Microsoft.Build.Construction
{
    /// <summary>
    /// Initializes a ProjectImportElement instance.
    /// </summary>
    [DebuggerDisplay("Project={Project} Condition={Condition}")]
    public class ProjectImportElement : ProjectElement
    {
        /// <summary>
        /// Initialize a parented ProjectImportElement
        /// </summary>
        internal ProjectImportElement(XmlElementWithLocation xmlElement, ProjectElementContainer parent, ProjectRootElement containingProject)
            : base(xmlElement, parent, containingProject)
        {
            ErrorUtilities.VerifyThrowArgumentNull(parent, "parent");
        }

        /// <summary>
        /// Initialize an unparented ProjectImportElement
        /// </summary>
        private ProjectImportElement(XmlElementWithLocation xmlElement, ProjectRootElement containingProject)
            : base(xmlElement, null, containingProject)
        {
        }

        /// <summary>
        /// Gets or sets the Project value. 
        /// </summary>
        public string Project
        {
            get
            {
                return
                    FileUtilities.FixFilePath(ProjectXmlUtilities.GetAttributeValue(XmlElement, XMakeAttributes.project));
            }

            set
            {
                ErrorUtilities.VerifyThrowArgumentLength(value, XMakeAttributes.project);

                ProjectXmlUtilities.SetOrRemoveAttribute(XmlElement, XMakeAttributes.project, value);
                MarkDirty("Set Import Project {0}", value);
            }
        }

        /// <summary>
        /// Location of the project attribute
        /// </summary>
        /// <remarks>
        /// For an implicit import, the location points to the Sdk attribute on the Project element.
        /// </remarks>
        public ElementLocation ProjectLocation => XmlElement.GetAttributeLocation(XMakeAttributes.project);

        public string Sdk
        {
            get { return ProjectXmlUtilities.GetAttributeValue(XmlElement, XMakeAttributes.sdk); }
            set
            {
                ProjectXmlUtilities.SetOrRemoveAttribute(XmlElement, XMakeAttributes.sdk, value);
                MarkDirty("Set Sdk {0}", value);
            }
        }

        public ElementLocation SdkLocation => XmlElement.GetAttributeLocation(XMakeAttributes.sdk);

        /// <summary>
        /// Gets the Implicit state of the element: true if the element was not in the read XML.
        /// </summary>
        // TODO: *should* this be public? if it's not, you can't determine if an import is implicit from the public OM.
        public bool Implicit
        {
            get { return XmlElement.HasAttribute(XMakeAttributes.@implicit); }
        }

        /// <summary>
        /// Creates an unparented ProjectImportElement, wrapping an unparented XmlElement.
        /// Validates the project value.
        /// Caller should then ensure the element is added to a parent
        /// </summary>
        internal static ProjectImportElement CreateDisconnected(string project, ProjectRootElement containingProject)
        {
            XmlElementWithLocation element = containingProject.CreateElement(XMakeElements.import);

            ProjectImportElement import = new ProjectImportElement(element, containingProject);

            import.Project = project;

            return import;
        }

        /// <summary>
        /// Overridden to verify that the potential parent and siblings
        /// are acceptable. Throws InvalidOperationException if they are not.
        /// </summary>
        internal override void VerifyThrowInvalidOperationAcceptableLocation(ProjectElementContainer parent, ProjectElement previousSibling, ProjectElement nextSibling)
        {
            ErrorUtilities.VerifyThrowInvalidOperation(parent is ProjectRootElement || parent is ProjectImportGroupElement, "OM_CannotAcceptParent");
        }

        /// <inheritdoc />
        protected override ProjectElement CreateNewInstance(ProjectRootElement owner)
        {
            return owner.CreateImportElement(this.Project);
        }
    }
}
