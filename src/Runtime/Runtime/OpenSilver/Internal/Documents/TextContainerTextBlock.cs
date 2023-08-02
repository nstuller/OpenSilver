﻿
/*===================================================================================
* 
*   Copyright (c) Userware/OpenSilver.net
*      
*   This file is part of the OpenSilver Runtime (https://opensilver.net), which is
*   licensed under the MIT license: https://opensource.org/licenses/MIT
*   
*   As stated in the MIT license, "the above copyright notice and this permission
*   notice shall be included in all copies or substantial portions of the Software."
*  
\*====================================================================================*/

using System;
using CSHTML5.Internal;

#if MIGRATION
using System.Windows.Controls;
using System.Windows.Documents;
#else
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
#endif

namespace OpenSilver.Internal.Documents;

internal sealed class TextContainerTextBlock : TextContainer<TextBlock>
{
    internal TextContainerTextBlock(TextBlock parent)
        : base(parent)
    {
    }

    public override string Text
    {
        get
        {
            string text = string.Empty;
            foreach (Inline inline in Parent.Inlines)
            {
                if (inline is Run)
                {
                    text += ((Run)inline).Text;
                }
                else if (inline is Span)
                {
                    text += new TextContainerSpan(((Span)inline)).Text;
                }
                else
                {
                    //do nothing
                    //note: should we throw an exception ?
                }
            }
            return text;
        }
    }

    public override void EndChange()
    {
        Parent.SetTextPropertyNoCallBack(Text);
    }

    protected override void OnTextAddedOverride(TextElement textElement)
    {
        if (INTERNAL_VisualTreeManager.IsElementInVisualTree(Parent))
        {
            INTERNAL_VisualTreeManager.AttachVisualChildIfNotAlreadyAttached(textElement, Parent);
        }
    }

    protected override void OnTextRemovedOverride(TextElement textElement)
    {
        if (INTERNAL_VisualTreeManager.IsElementInVisualTree(Parent))
        {
            INTERNAL_VisualTreeManager.DetachVisualChildIfNotNull(textElement, Parent);
        }
    }
}