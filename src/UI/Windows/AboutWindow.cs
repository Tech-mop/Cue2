// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using System.Collections.Generic;
using System.Linq;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.UI.Windows;

public partial class AboutWindow : Window
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    
    // Ui
    private Label _versionLabel;
    private Label _copyrightLabel;
    private RichTextLabel _authorsRichTextLabel;
    private RichTextLabel _licenseRichTextLabel;
    private LinkButton _cue2LicenseLinkButton;

    private HSplitContainer _thirdpartyLicensesSplitContainer;
    private VBoxContainer _menuVBox;
    private RichTextLabel _thirdPartyLicenseRichTextLabel;
    private LinkButton _licenseLinkButton;

    /// <summary>
    /// Applies Linux native-window policy before the scene window enters the tree.
    /// </summary>
    public AboutWindow()
    {
        LinuxWindowEmbedPolicy.ApplyToAppWindow(this);
    }
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        
        UiUtilities.RescaleWindow(this, _globalData.BaseDisplayScale);
        float userScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
        UiUtilities.RescaleUi(this, userScale, _globalData.BaseDisplayScale);
            
        _globalSignals.UiScaleChanged += ScaleUi;
        
        // Define ui properties
        _versionLabel = GetNode<Label>("%VersionLabel");
        _copyrightLabel = GetNode<Label>("%CopyrightLabel");
        _authorsRichTextLabel = GetNode<RichTextLabel>("%AuthorsRichTextLabel");
        _licenseRichTextLabel = GetNode<RichTextLabel>("%LicenseRichTextLabel");
        _cue2LicenseLinkButton = GetNode<LinkButton>("%Cue2LicenseLinkButton");
        _thirdpartyLicensesSplitContainer = GetNode<HSplitContainer>("%Third-party Licenses");
        _menuVBox = GetNode<VBoxContainer>("%MenuVBox");
        _thirdPartyLicenseRichTextLabel = GetNode<RichTextLabel>("%LicensesRichTextLabel");
        _licenseLinkButton = GetNode<LinkButton>("%LicenseLinkButton");

        _versionLabel.Text = $"Cue2 {Version.FullVersionString}";
        _copyrightLabel.Text = UiLocalizer.T("Copyright © 2025-2026 Samuel Moxham");

        _authorsRichTextLabel.Text = Authors;

        _licenseRichTextLabel.Text = Cue2License;
        _cue2LicenseLinkButton.Uri = Version.Website;
        

        PopulateThirdPartyLicenses();
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
    }

    /// <summary>
    /// Re-localizes About window chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

    private void PopulateThirdPartyLicenses()
    {
        var dependencies = new Dictionary<string, (string License, string Url)>
        {
            { "FFmpeg.AutoGen v9.0.1.1", (FfmpegLicenseAutogen, "https://github.com/Ruslan-B/FFmpeg.AutoGen/blob/9.0/LICENSE.txt") },
            { "FFmpeg (native libraries)", (FfmpegNativeLicense, "https://ffmpeg.org/legal.html") },
            { "Godot v4.7.1", (GodotLicense, "https://github.com/godotengine/godot/blob/master/LICENSE.txt") },
            { "RtMidi v6.0.0", (RtMidiLicense, "https://github.com/thestk/rtmidi/blob/master/LICENSE") },
            { "SDL3-CS v3.4.14.1", (Sdl3CsLicense, "https://github.com/edwardgushchin/SDL3-CS/blob/master/LICENSE") },
            { "Rug.Osc v1.2.5", (RugOscLicense, "https://bitbucket.org/rugcode/rug.osc/wiki/License") }
        };

        foreach (var dep in dependencies)
        {
            var button = new Button { Text = dep.Key };
            button.Pressed += () => ShowLicense(dep.Value.License, dep.Value.Url);
            _menuVBox.AddChild(button);
        }
    }   

    private void ShowLicense(string licenseText, string url)
    {
        _thirdPartyLicenseRichTextLabel.Text = licenseText;
        _licenseLinkButton.Uri = url;
        _licenseLinkButton.Visible = true;
    }
    
    private void ScaleUi(float value)
    {
        try
        {
            float effectiveScale = value * _globalData.BaseDisplayScale;
            WrapControls = true;
            ContentScaleFactor = effectiveScale;
            ChildControlsChanged();
            GD.Print($"LogWindow:_scaleUI - Applied effective UI scale: {effectiveScale} (user: {value} * base: {_globalData.BaseDisplayScale})");
        } 
        catch (Exception ex)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error applying UI scale: {ex.Message}", 2);
            GetWindow().ContentScaleFactor = value; // Fallback to original value without multiplier
        }
    }
    
    public override void _ExitTree()
    {
        if (_globalSignals != null)
        {
            _globalSignals.UiScaleChanged -= ScaleUi;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        }
    }

    private const string Colour = "#974B08";
    // Authors text
    private const string Authors = $@"[b][color={Colour}]Project Founder[/color][/b]

    Samuel Moxham (info@cue2.live)

[b][color={Colour}]Project Manager[/color][/b]

    Chris Twyman

[b][color={Colour}]Developers[/color][/b]

    Community Contributors: Thanks to all who have helped with testing and feedback

[i]Built with love for the performing arts community.[/i]";

    // License texts
    private const string Cue2License = @"Cue2 v0.1 : MIT License

Copyright © 2025-2026 Samuel Moxham

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";
    
    
    private const string GodotLicense = @"Godot v4.7.1 : MIT License

Copyright © 2014-present Godot Engine contributors.
Copyright © 2007-2014 Juan Linietsky, Ariel Manzur.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

 -- Godot Engine <https://godotengine.org>";

    private const string FfmpegLicenseAutogen = @"FFmpeg.AutoGen v9.0.1.1 : MIT License

Copyright © 2025 Ruslan Balanukhin (Rationale One)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";

    private const string FfmpegNativeLicense = @"FFmpeg native libraries (avcodec, avformat, etc.)

This software uses libraries from the FFmpeg project under the
GNU Lesser General Public License version 2.1 or later (LGPLv2.1+).

FFmpeg itself is licensed under the LGPL (core libraries) with some optional
components under the GPL. The bundled libraries in this project are intended
to be built using only LGPL-compatible options (no --enable-gpl, no --enable-nonfree).

You can obtain the corresponding source code for the exact version of FFmpeg
used to build these libraries from the project releases or by following the
instructions in docs/FFmpeg-Licensing.md.

Full FFmpeg legal information: https://ffmpeg.org/legal.html
LGPLv2.1 text: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

Copyright © 2000-2025 the FFmpeg developers";

    private const string Sdl3CsLicense = @"SDL3-CS v3.4.14.1 : zlib License

Copyright © 2024-2025 Eduard Gushchin <eduardgushchin@yandex.ru>
  
This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
  
1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required. 
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.";

    private const string RugOscLicense = @"Rug.Osc v1.2.5 : MIT License

Copyright © 2013 Phill Tew (peatew@gmail.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

    private const string RtMidiLicense = @"RtMidi v6.0.0 : MIT-style License

RtMidi: realtime MIDI i/o C++ classes
Copyright (c) 2003-2023 Gary P. Scavone

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation files
(the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software,
and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

Any person wishing to distribute modifications to the Software is
asked to send the modifications to the original developer so that
they can be incorporated into the canonical version.  This is,
however, not a binding provision of this license.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

This product uses RtMidi for MIDI device I/O. Platform natives
(rtmidi.dll / librtmidi.dylib / librtmidi.so) are loaded from
res://bin/ via NativeLibPaths.

Project: https://github.com/thestk/rtmidi";
}