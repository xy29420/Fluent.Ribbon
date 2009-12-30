﻿#region Copyright and License Information
// Fluent Ribbon Control Suite
// http://fluent.codeplex.com/
// Copyright © Degtyarev Daniel, Rikker Serg. 2009-2010.  All rights reserved.
// 
// Distributed under the terms of the Microsoft Public License (Ms-PL). 
// The license is available online http://fluent.codeplex.com/license
#endregion
using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Collections.Generic;
using System.Windows.Input;
using System.Linq;
using System.Text;

namespace Fluent
{
    /// <summary>
    /// Represents adorner for KeyTips. 
    /// KeyTipAdorners is chained to produce one from another. 
    /// Detaching root adorner couses detaching all adorners in the chain
    /// </summary>
    internal class KeyTipAdorner : Adorner
    {
        #region Events

        /// <summary>
        /// This event is occured when adorner is 
        /// detached and is not able to be attached again
        /// </summary>
        public event EventHandler Terminated;

        #endregion

        #region Fields

        // KeyTips that have been
        // found on this element
        List<KeyTip> keyTips = new List<KeyTip>();        
        List<UIElement> associatedElements = new List<UIElement>();
        Point[] keyTipPositions;

        // Parent adorner
        KeyTipAdorner parentAdorner;
        KeyTipAdorner childAdorner;

        // Focused element
        UIElement focusedElement;
        

        // Is this adorner attached to the adorned element?
        bool attached;
        HwndSource attachedHwndSource;

        // Current entered chars
        string enteredKeys = "";

        // Designate that this adorner is terminated
        bool terminated;

        DispatcherTimer timerFocusTracking;

        AdornerLayer adornerLayer;

        #endregion

        #region Properties

        /// <summary>
        /// Determines whether at least one on the adorners in the chain is alive
        /// </summary>
        public bool IsAdornerChainAlive
        {
            get { return attached || (childAdorner != null && childAdorner.IsAdornerChainAlive); }
        }

        #endregion

        #region Intialization

        /// <summary>
        /// Construcotor
        /// </summary>
        /// <param name="adornedElement"></param>
        /// <param name="parentAdorner">Parent adorner or null</param>
        /// <param name="keyTipElementContainer">The element which is container for elements</param>
        public KeyTipAdorner(UIElement adornedElement, UIElement keyTipElementContainer, KeyTipAdorner parentAdorner)
            : base(adornedElement)
        {
            this.parentAdorner = parentAdorner;

            // Try to find supported elements
            FindKeyTips(keyTipElementContainer, false);

            keyTipPositions = new Point[keyTips.Count];
        }

           
        // Find key tips on the given element
        void FindKeyTips(UIElement element, bool hide)
        {
            IEnumerable children = LogicalTreeHelper.GetChildren(element);
            foreach (object item in children)
            {
                UIElement child = item as UIElement;
                RibbonGroupBox groupBox = (child as RibbonGroupBox);
                if (child != null)
                {
                    string keys = KeyTip.GetKeys(child);
                    if (keys != null)
                    {
                        // Gotcha!
                        KeyTip keyTip = new KeyTip();
                        keyTip.Content = keys;
                        keyTip.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;  
                        

                        // Add to list & visual 
                        // children collections
                        keyTips.Add(keyTip);                        
                        base.AddVisualChild(keyTip);
                        associatedElements.Add(child);


                        
                        if (groupBox!=null) 
                        {
                            if (groupBox.State == RibbonGroupBoxState.Collapsed)
                            {
                                keyTip.Visibility = Visibility.Visible;
                                FindKeyTips(child, true);
                                continue;
                            }
                            else keyTip.Visibility = Visibility.Collapsed;                            
                        }
                        else 
                        {
                            // Bind IsEnabled property
                            Binding binding = new Binding("IsEnabled");
                            binding.Source = child;
                            binding.Mode = BindingMode.OneWay;
                            keyTip.SetBinding(UIElement.IsEnabledProperty, binding);
                            continue;
                        }
                    }

                    if ((groupBox!=null) &&
                       (groupBox.State == RibbonGroupBoxState.Collapsed))
                            FindKeyTips(child, true);
                    else FindKeyTips(child, hide);
                }
            }
        }


        #endregion

        #region Attach & Detach

        /// <summary>
        /// Attaches this adorner to the adorned element
        /// </summary>
        public void Attach()
        {            
            if (attached) return;
            Log("Attach begin");

            FrameworkElement adornedElement = (FrameworkElement)AdornedElement;
            if (!adornedElement.IsLoaded)
            {
                // Delay attaching
                adornedElement.Loaded += OnDelayAttach;
                return;
            }
/*
            if (!(associatedElements[0] as FrameworkElement).IsLoaded)
            {
                // Delay attaching
                (associatedElements[0] as FrameworkElement).Loaded += OnDelayAttach;
                return;
            }*/
            
            // Focus current adorned element
           // Keyboard.Focus(adornedElement);
            focusedElement = Keyboard.FocusedElement as UIElement;
            if (focusedElement != null)
            {
                Log("Focus Attached to " + focusedElement.ToString());
                focusedElement.LostFocus += OnFocusLost;                
                focusedElement.PreviewKeyDown += OnPreviewKeyDown;
                focusedElement.PreviewKeyUp += OnPreviewKeyUp;
            }
            else Log("[!] Focus Setup Failed");
            GetTopLevelElement(AdornedElement).PreviewMouseDown += OnInputActionOccured;

            // Show this adorner
            adornerLayer = GetAdornerLayer(associatedElements[0]);
            adornerLayer.Add(this);
            // Clears previous user input
            enteredKeys = "";
            FilterKeyTips();
            

            // Hookup window activation
            attachedHwndSource = ((HwndSource)PresentationSource.FromVisual(adornedElement));
            if (attachedHwndSource != null) attachedHwndSource.AddHook(WindowProc);

            // Start timer to track focus changing
            if (timerFocusTracking == null)
            {
                timerFocusTracking = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher.CurrentDispatcher);
                timerFocusTracking.Interval = TimeSpan.FromMilliseconds(50);
                timerFocusTracking.Tick += OnTimerFocusTrackingTick;
            }
            timerFocusTracking.Start();

            attached = true;
            Log("Attach end");
        }

        void OnTimerFocusTrackingTick(object sender, EventArgs e)
        {
            if (focusedElement != Keyboard.FocusedElement)
            {
                Log("Focus is changed, but focus lost is not occured");
                if (focusedElement != null)
                {
                    focusedElement.LostFocus -= OnFocusLost;
                    focusedElement.PreviewKeyDown -= OnPreviewKeyDown;
                    focusedElement.PreviewKeyUp -= OnPreviewKeyUp;
                }
                focusedElement = (UIElement)Keyboard.FocusedElement;
                if (focusedElement != null)
                {
                    focusedElement.LostFocus += OnFocusLost;
                    focusedElement.PreviewKeyDown += OnPreviewKeyDown;
                    focusedElement.PreviewKeyUp += OnPreviewKeyUp;
                }
            }
        }

        // Window's messages hook up
        IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Check whether window is deactivated (wParam == 0)
            if ((msg == 6) && (wParam == IntPtr.Zero) && (attached))
            {
                Log("The host window is deactivated, keytips will be terminated");
                Terminate();
            }
            return IntPtr.Zero;
        }

        void OnDelayAttach(object sender, EventArgs args)
        {
            ((FrameworkElement)AdornedElement).Loaded -= OnDelayAttach;
            ((FrameworkElement)associatedElements[0]).Loaded -= OnDelayAttach;
            Attach();
        }

        /// <summary>
        /// Detaches this adorner from the adorned element
        /// </summary> 
        public void Detach()
        {
            if (childAdorner != null) childAdorner.Detach();            
            if (!attached) return;


            Log("Detach Begin");

            // Remove window hookup
            if ((attachedHwndSource != null)&&(!attachedHwndSource.IsDisposed))
            {
                // Crashes in a few time if invoke immediately ???
                AdornedElement.Dispatcher.BeginInvoke((System.Threading.ThreadStart)delegate { attachedHwndSource.RemoveHook(WindowProc); });
            }

            // Maybe adorner awaiting attaching, cancel it
            ((FrameworkElement)AdornedElement).Loaded -= OnDelayAttach;
                        
            if (focusedElement != null)
            {                
                focusedElement.LostFocus -= OnFocusLost;
                focusedElement.PreviewKeyDown -= OnPreviewKeyDown;
                focusedElement.PreviewKeyUp -= OnPreviewKeyUp;
                focusedElement = null;
            }

            GetTopLevelElement(AdornedElement).PreviewMouseDown -= OnInputActionOccured;

            // Show this adorner
            adornerLayer.Remove(this);
            // Clears previous user input
            enteredKeys = "";
            attached = false;

            // Stop timer to track focus changing
            timerFocusTracking.Stop();

            Log("Detach End");
        }


        #endregion

        #region Termination

        /// <summary>
        /// Terminate whole key tip's adorner chain
        /// </summary>
        public void Terminate()
        {
            if (terminated) return;
            terminated = true;

            Detach();
            if (parentAdorner != null) parentAdorner.Terminate();
            if (childAdorner != null) childAdorner.Terminate();
            if (Terminated != null) Terminated(this, EventArgs.Empty);

            Log("Termination");
        }

        #endregion

        #region Event Handlers

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Log("Key Down " + e.Key.ToString() + " ("+ e.OriginalSource.ToString() + ")");
            if (Visibility == Visibility.Hidden) return;

            if ((!(AdornedElement is ContextMenu)) && 
                ((e.Key == Key.Left) || (e.Key == Key.Right) || (e.Key == Key.Up) || (e.Key == Key.Down) || 
                (e.Key == Key.NumPad0) || (e.Key == Key.NumPad1) || (e.Key == Key.NumPad2) || 
                (e.Key == Key.NumPad3) || (e.Key == Key.NumPad4) || (e.Key == Key.NumPad5) ||
                (e.Key == Key.NumPad6) || (e.Key == Key.NumPad7) || (e.Key == Key.NumPad7) ||
                (e.Key == Key.NumPad8) || (e.Key == Key.NumPad9) || 
                (e.Key == Key.Enter) || (e.Key == Key.Tab)))
            {
                 Visibility = Visibility.Hidden;
            }
            else if (e.Key == Key.Escape) Back();
            else 
            {
                string newKey = (new KeyConverter()).ConvertToString(e.Key);
                if ((newKey.Length == 1)&&(Char.IsLetterOrDigit(newKey, 0)))
                {
                    e.Handled = true;
                    if (IsElementsStartWith(enteredKeys + newKey))
                    {
                        enteredKeys += newKey;
                        UIElement element = TryGetElement(enteredKeys);
                        if (element != null) Forward(element);
                        else FilterKeyTips();
                    }
                    else System.Media.SystemSounds.Beep.Play();
                }                
            }
        }

        void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            
        }

        void OnInputActionOccured(object sender, RoutedEventArgs e)
        {
            if (attached)
            {
                Log("Input Action, Keystips will be terminated");
                Terminate();
            }
        }

        void OnFocusLost(object sender, RoutedEventArgs e)
        {
            if (attached)
            {
                Log("Focus Lost");  
                UIElement previousFocusedElementElement = focusedElement;
                focusedElement.LostFocus -= OnFocusLost;
                focusedElement.PreviewKeyDown -= OnPreviewKeyDown;
                focusedElement.PreviewKeyUp -= OnPreviewKeyUp;
                focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    Log("Focus Changed from " + previousFocusedElementElement.ToString() + " to " + focusedElement.ToString());
                    focusedElement.LostFocus += OnFocusLost;
                    focusedElement.PreviewKeyDown += OnPreviewKeyDown;
                    focusedElement.PreviewKeyUp += OnPreviewKeyUp;
                }
                else Log("Focus Not Restored"); 
            }
        }

        #endregion

        #region Static Methods

        static AdornerLayer GetAdornerLayer(UIElement element)
        {
            UIElement current = element;
            while (true)
            {
                current = (UIElement)VisualTreeHelper.GetParent(current);
                if (current is AdornerDecorator) return AdornerLayer.GetAdornerLayer((UIElement)VisualTreeHelper.GetChild(current, 0));
            }
        }

        static UIElement GetTopLevelElement(UIElement element)
        {
            UIElement current = element;
            while (true)
            {
                current = (VisualTreeHelper.GetParent(element)) as UIElement;
                if (current == null) return element;
                element = current;
            }
        }

        #endregion

        #region Methods

        // Back to the previous adorner
        void Back()
        {            
            if (parentAdorner != null)
            {
                Log("Back");
                Detach();
                parentAdorner.Attach();
            }
            else Terminate();
        }

        /// <summary>
        /// Forwards to the elements with the given keys
        /// </summary>
        /// <param name="keys">Keys</param>
        /// <param name="click">If true the element will be clicked</param>
        /// <returns>If the element will be found the function will return true</returns>
        public bool Forward(string keys, bool click)
        {
            UIElement element = TryGetElement(keys);
            if (element == null) return false;
            Forward(element, click);
            return true;
        }

        // Forward to the next element
        void Forward(UIElement element) { Forward(element, true); }

        // Forward to the next element
        void Forward(UIElement element, bool click)
        {
            Detach();
            if (click)
            {
                element.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, null));
                element.UpdateLayout();
            }

            UIElement[] children = LogicalTreeHelper.GetChildren(element)
                .Cast<object>()
                .Where(x => x is UIElement)
                .Cast<UIElement>().ToArray();
            if (children.Length == 0) { Terminate(); return; }
                    
            if (GetTopLevelElement(children[0]) != GetTopLevelElement(element))
                childAdorner = new KeyTipAdorner(children[0], element, this);
            else
                childAdorner = new KeyTipAdorner(element, element, this);

            if (childAdorner.keyTips.Count != 0)
            {
                Detach();
                childAdorner.Attach();
            }
            else Terminate();
        }

        

        /// <summary>
        /// Gets element keytipped by keys
        /// </summary>
        /// <param name="keys"></param>
        /// <returns>Element</returns>
        UIElement TryGetElement(string keys)
        {
            for(int i = 0; i < keyTips.Count; i++)
            {
                if (!keyTips[i].IsEnabled) continue;
                string keysUpper = keys.ToUpper(CultureInfo.CurrentUICulture);
                string contentUpper = (keyTips[i].Content as string).ToUpper(CultureInfo.CurrentUICulture);
                if (keysUpper == contentUpper) return associatedElements[i];
            }
            return null;
        }

        /// <summary>
        /// Is one of the elements starts with the given chars
        /// </summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        public bool IsElementsStartWith(string keys)
        {
            for (int i = 0; i < keyTips.Count; i++)
            {
                if (!keyTips[i].IsEnabled) continue;
                string keysUpper = keys.ToUpper(CultureInfo.CurrentUICulture);
                string contentUpper = (keyTips[i].Content as string).ToUpper(CultureInfo.CurrentUICulture);
                if (contentUpper.StartsWith(keysUpper, StringComparison.CurrentCulture)) return true;
            }
            return false;
        }

        Visibility[] backupedVisibilities;
        // Hide / unhide keytips relative matching to entered keys
        void FilterKeyTips()
        {
            if (backupedVisibilities == null)
            {
                // Backup current visibility of key tips
                backupedVisibilities = new Visibility[keyTips.Count];
                for (int i = 0; i < backupedVisibilities.Length; i++)
                {
                    backupedVisibilities[i] = keyTips[i].Visibility;
                }
            }

            // Hide / unhide keytips relative matching to entered keys
            for (int i = 0; i < keyTips.Count; i++)
            {
                string keys = (string)keyTips[i].Content;
                if (string.IsNullOrEmpty(enteredKeys)) keyTips[i].Visibility = backupedVisibilities[i];
                else keyTips[i].Visibility = keys.StartsWith(enteredKeys,StringComparison.CurrentCulture) ? backupedVisibilities[i] : Visibility.Collapsed;
            }
        }

        #endregion

        #region Layout & Visual Children

        /// <summary>
        /// Positions child elements and determines
        /// a size for the control
        /// </summary>
        /// <param name="finalSize">The final area within the parent 
        /// that this element should use to arrange 
        /// itself and its children</param>
        /// <returns>The actual size used</returns>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Rect rect = new Rect(finalSize);

            for (int i = 0; i < keyTips.Count; i++)
            {
                keyTips[i].Arrange(new Rect(keyTipPositions[i], keyTips[i].DesiredSize));
            }

            return finalSize;
        }
               
        /// <summary>
        /// Measures KeyTips
        /// </summary>
        /// <param name="constraint">The available size that this element can give to child elements.</param>
        /// <returns>The size that the groups container determines it needs during 
        /// layout, based on its calculations of child element sizes.
        /// </returns>
        protected override Size MeasureOverride(Size constraint)
        {            
            Size infinitySize = new Size(Double.PositiveInfinity, Double.PositiveInfinity);
            foreach (KeyTip tip in keyTips) tip.Measure(infinitySize);
            UpdateKeyTipPositions();

            Size result = new Size(0, 0);
            for (int i = 0; i < keyTips.Count; i++)
            {
                double cornerX = keyTips[i].DesiredSize.Width + keyTipPositions[i].X;
                double cornerY = keyTips[i].DesiredSize.Height + keyTipPositions[i].Y;
                if (cornerX > result.Width) result.Width = cornerX;
                if (cornerY > result.Height) result.Height = cornerY;
            }
            return result;
        }

        /// <summary>
        /// Gets parent RibbonGroupBox or null
        /// </summary>
        /// <param name="element"></param>
        /// <returns></returns>
        RibbonGroupBox GetGroupBox(DependencyObject element)
        {
            if (element == null) return null;
            RibbonGroupBox groupBox = element as RibbonGroupBox;
            if (groupBox != null) return groupBox;
            DependencyObject parent = VisualTreeHelper.GetParent(element);
            return GetGroupBox(parent);
        }

        void UpdateKeyTipPositions()
        {
            if (keyTips.Count == 0) return;

            double[] rows = null;
            RibbonGroupBox groupBox = GetGroupBox(associatedElements[0]);
            if (groupBox != null)
            {
                Panel panel = groupBox.GetPanel();
                if (panel != null)
                {
                    double height = groupBox.DesiredSize.Height;
                    rows = new double[]
                        {
                            groupBox.TranslatePoint(new Point(0, 0), AdornedElement).Y,
                            groupBox.TranslatePoint(new Point(0, panel.DesiredSize.Height / 2.0), AdornedElement).Y,
                            groupBox.TranslatePoint(new Point(0, panel.DesiredSize.Height), AdornedElement).Y,
                            groupBox.TranslatePoint(new Point(0, height), AdornedElement).Y                            
                        };
                }
            }

            for (int i = 0; i < keyTips.Count; i++)
            {
                if ((associatedElements[i] is RibbonTabItem) || (associatedElements[i] is BackstageButton))
                {
                    // Ribbon Tab Item Exclusive Placement
                    Size keyTipSize = keyTips[i].DesiredSize;
                    Size elementSize = associatedElements[i].RenderSize;
                    keyTipPositions[i] = associatedElements[i].TranslatePoint(
                        new Point(elementSize.Width / 2.0 - keyTipSize.Width / 2.0,
                            elementSize.Height - keyTipSize.Height / 2.0), AdornedElement);
                }
                else if (associatedElements[i] is RibbonGroupBox)
                {
                    // Ribbon Group Box Exclusive Placement
                    Size keyTipSize = keyTips[i].DesiredSize;
                    Size elementSize = associatedElements[i].DesiredSize;
                    keyTipPositions[i] = associatedElements[i].TranslatePoint(
                        new Point(elementSize.Width / 2.0 - keyTipSize.Width / 2.0,
                            elementSize.Height + 1), AdornedElement);
                }
                else if (IsWithinQuickAccessToolbar(associatedElements[i]))
                {
                    Point translatedPoint = associatedElements[i].TranslatePoint(new Point(
                            associatedElements[i].DesiredSize.Width / 2.0 - keyTips[i].DesiredSize.Width / 2.0,
                            associatedElements[i].DesiredSize.Height - keyTips[i].DesiredSize.Height / 2.0), AdornedElement);
                    keyTipPositions[i] = translatedPoint;
                }
                else
                {
                    if ((associatedElements[i] is RibbonControl)&&((associatedElements[i] as RibbonControl).Size != RibbonControlSize.Large))
                    {
                        Point translatedPoint = associatedElements[i].TranslatePoint(new Point(keyTips[i].DesiredSize.Width / 2.0, keyTips[i].DesiredSize.Height / 2.0), AdornedElement);
                        // Snapping to rows if it present
                        if (rows != null)
                        {
                            int index = 0;
                            double mindistance = Math.Abs(rows[0] - translatedPoint.Y);
                            for (int j = 1; j < rows.Length; j++)
                            {
                                double distance = Math.Abs(rows[j] - translatedPoint.Y);
                                if (distance < mindistance)
                                {
                                    mindistance = distance;
                                    index = j;
                                }
                            }
                            translatedPoint.Y = rows[index] - keyTips[i].DesiredSize.Height / 2.0;
                        }
                        keyTipPositions[i] = translatedPoint;
                    }
                    else
                    {
                        Point translatedPoint = associatedElements[i].TranslatePoint(new Point(
                            associatedElements[i].DesiredSize.Width / 2.0 - keyTips[i].DesiredSize.Width / 2.0,
                            associatedElements[i].DesiredSize.Height - 8), AdornedElement);
                        if (rows != null) translatedPoint.Y = rows[2] - keyTips[i].DesiredSize.Height / 2.0;
                        keyTipPositions[i] = translatedPoint;
                    }
                }
            }
        }

        // Determines whether the element is children to quick access toolbar
        bool IsWithinQuickAccessToolbar(UIElement element)
        {
            UIElement parent = LogicalTreeHelper.GetParent(element) as UIElement;
            if (parent is QuickAccessToolBar) return true;
            if (parent == null) return false;
            return IsWithinQuickAccessToolbar(parent);
        }

        /// <summary>
        /// Gets visual children count
        /// </summary>
        protected override int VisualChildrenCount
        {
            get
            {
                return this.keyTips.Count;
            }
        }

        /// <summary>
        /// Returns a child at the specified index from a collection of child elements
        /// </summary>
        /// <param name="index">The zero-based index of the requested child element in the collection</param>
        /// <returns>The requested child element</returns>
        protected override Visual GetVisualChild(int index)
        {
            return this.keyTips[index];
        }

        #endregion

        #region Logging

        [SuppressMessage("Microsoft.Performance", "CA1822")]
        [SuppressMessage("Microsoft.Performance", "CA1801")]
        void Log(string message)
        {
            // Uncomment in case of emergency
            // System.Diagnostics.Debug.WriteLine("[" + AdornedElement.GetType().Name + "] " + message);
        }

        #endregion
    }
}
