﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace CTA.Rules.Common.Extensions
{
    public static class XDocumentExtensions
    {
        public static bool ContainsElementPath(this XDocument document, string elementPath)
        {
            return document.XPathSelectElements(elementPath).Any();
        }

        public static bool ContainsAttribute(this XDocument document, string elementPath, string attributeName)
        {
            return document.XPathSelectElements(elementPath).Any(e => e.Attribute(attributeName) != null);
        }

        public static bool ContainsAttributeValue(this XDocument document, string elementPath, string attributeName, string value)
        {
            return document.XPathSelectElements(elementPath).Any(e => e.Attribute(attributeName)?.Value == value);
        }

        public static IEnumerable<XElement> GetElementsByLocalName(this XDocument document, string elementName)
        {
            return document.Descendants().Where(d => string.Equals(d.Name.LocalName, elementName, StringComparison.InvariantCulture));
        }
    }
}
