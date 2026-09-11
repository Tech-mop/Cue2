// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;

namespace Cue2.Services;


/// <summary>
/// Provides utility methods for loading and instantiating Godot scenes.
/// This class handles scene loading with caching support, error handling, and logging via GlobalSignals.
/// </summary>
public static class SceneLoader
{
    
    /// <summary>
    /// Loads a Godot scene from the specified path, retrieves it from cache if available, and instantiates it.
    /// </summary>
    /// <param name="path">The file path to the scene resource (e.g., .tscn file).</param>
    /// <param name="errorMessage">An output parameter that contains an error message if the operation fails.</param>
    /// <returns>The instantiated <see cref="Node"/> from the scene, or <c>null</c> if loading or instantiation fails.</returns>
    /// <exception cref="ArgumentException">Thrown if the path is null or empty.</exception>
    /// <exception cref="Exception">Thrown for other unexpected errors during scene instantiation.</exception>
    public static Node LoadScene(string path, out string errorMessage)
    {
        PackedScene scene = LoadPackedSceneInternal(path, out errorMessage);
        if (scene == null)
        {
            return null; // Error message already set by LoadPackedSceneInternal
        }

        try
        {
            Node instance = scene.Instantiate();
            if (instance == null)
            {
                errorMessage = $"Failed to instantiate scene from path: {path}.";
                return null;
            }

            return instance;
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred while instantiating scene at path: {path}. Exception: {ex.Message}";
            return null;
        }
    }


    /// <summary>
    /// Loads a Godot <see cref="PackedScene"/> from the specified path or retrieves it from cache if available.
    /// This method does not instantiate the scene.
    /// </summary>
    /// <param name="path">The file path to the scene resource (e.g., .tscn file).</param>
    /// <param name="errorMessage">An output parameter that contains an error message if the operation fails.</param>
    /// <returns>The loaded <see cref="PackedScene"/>, or <c>null</c> if loading fails.</returns>
    /// <exception cref="ArgumentException">Thrown if the path is null or empty.</exception>
    /// <exception cref="Exception">Thrown for other unexpected errors during scene loading.</exception>
    public static PackedScene LoadPackedScene(string path, out string errorMessage)
    {
        return LoadPackedSceneInternal(path, out errorMessage);
    }
    
    private static PackedScene LoadPackedSceneInternal(string path, out string errorMessage)
    {
        errorMessage = null;

        // Validate input
        if (string.IsNullOrEmpty(path))
        {
            errorMessage = "Scene path cannot be null or empty.";
            return null;
        }

        try
        {
            PackedScene scene;

            if (ResourceLoader.HasCached(path))
            {
                scene = (PackedScene)ResourceLoader.Load(path);
                //GD.Print($"Retrieved cached PackedScene: {path}");
            }
            else
            {
                scene = GD.Load<PackedScene>(path);
                //GD.Print($"Loaded PackedScene freshly and cached it: {path}");
            }

            if (scene == null)
            {
                errorMessage = $"Failed to load PackedScene at path: {path}. Resource not found or invalid.";
                return null;
            }

            return scene;
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred while loading PackedScene at path: {path}. Exception: {ex.Message}";
            return null;
        }
    }
}