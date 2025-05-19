﻿using Autodesk.ModelDerivative.Model;
using System;
using System.Collections.Generic;

public class ManifestHelper
{
    private readonly Manifest _manifest;

    public ManifestHelper(Manifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    /// <summary>
    /// Searches for derivatives matching the specified criteria.
    /// </summary>
    /// <param name="guid">Optional GUID to match.</param>
    /// <param name="type">Optional type to match.</param>
    /// <param name="role">Optional role to match.</param>
    /// <returns>List of matching DerivativeChild objects.</returns>
    public List<ManifestResources> Search(string guid = null, string type = null, string role = null)
    {
        var matches = new List<ManifestResources>();

        Traverse(child =>
        {
            bool guidMatch = string.IsNullOrEmpty(guid) || child.Guid == guid;
            bool typeMatch = string.IsNullOrEmpty(type) || child.Type == type;
            bool roleMatch = string.IsNullOrEmpty(role) || child.Role == role;

            if (guidMatch && typeMatch && roleMatch)
            {
                matches.Add(child);
            }

            return true; // Continue traversal
        });

        return matches;
    }

    /// <summary>
    /// Traverses all derivatives, executing the input callback for each one.
    /// </summary>
    /// <param name="callback">Function to be called for each derivative,
    /// returning a bool indicating whether the traversal should recurse deeper in the manifest hierarchy.</param>
    public void Traverse(Func<ManifestResources, bool> callback)
    {
        if (_manifest?.Derivatives == null)
            return;

        foreach (var derivative in _manifest.Derivatives)
        {
            if (derivative.Children != null)
            {
                foreach (var child in derivative.Children)
                {
                    Process(child, callback);
                }
            }
        }
    }

    private void Process(ManifestResources node, Func<ManifestResources, bool> callback)
    {
        bool proceed = callback(node);
        if (proceed && node.Children != null)
        {
            foreach (var child in node.Children)
            {
                Process(child, callback);
            }
        }
    }
}
