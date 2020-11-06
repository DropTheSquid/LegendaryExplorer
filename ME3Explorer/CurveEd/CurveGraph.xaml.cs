﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shapes;

namespace ME3Explorer.CurveEd
{
    /// <summary>
    /// Interaction logic for CurveGraph.xaml
    /// </summary>
    public partial class CurveGraph : UserControl
    {

        private const int LINE_SPACING = 50;

        private bool dragging;
        private Point dragPos;

        public event RoutedPropertyChangedEventHandler<CurvePoint> SelectedPointChanged;
        public static bool TrackLoading;
        public Curve SelectedCurve
        {
            get => (Curve)GetValue(SelectedCurveProperty);
            set => SetValue(SelectedCurveProperty, value);
        }

        // Using a DependencyProperty as the backing store for SelectedCurve.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty SelectedCurveProperty =
            DependencyProperty.Register(nameof(SelectedCurve), typeof(Curve), typeof(CurveGraph), new PropertyMetadata(new Curve(), OnSelectedCurveChanged));

        private static void OnSelectedCurveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is CurveGraph c)
            {
                TrackLoading = true;
                c.SelectedPoint = c.SelectedCurve.CurvePoints.FirstOrDefault();
                TrackLoading = false;
            }
        }

        public CurvePoint SelectedPoint
        {
            get => (CurvePoint)GetValue(SelectedPointProperty);
            set => SetValue(SelectedPointProperty, value);
        }

        // Using a DependencyProperty as the backing store for SelectedPoint.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty SelectedPointProperty =
            DependencyProperty.Register(nameof(SelectedPoint), typeof(CurvePoint), typeof(CurveGraph), new PropertyMetadata(new CurvePoint(0, 0, 0, 0, CurveMode.CIM_Linear), OnSelectedPointChanged));

        private static void OnSelectedPointChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is CurveGraph c)
            {
                foreach (var o in c.graph.Children)
                {
                    if (o is Anchor a)
                    {
                        if (a.point.Value != e.NewValue as CurvePoint)
                        {
                            a.IsSelected = false;
                        }
                    }
                }
                c.SelectedPointChanged?.Invoke(c, new RoutedPropertyChangedEventArgs<CurvePoint>(e.OldValue as CurvePoint, e.NewValue as CurvePoint));
            }
        }
        public double VerticalScale
        {
            get => (double)GetValue(VerticalScaleProperty);
            set => SetValue(VerticalScaleProperty, value);
        }

        // Using a DependencyProperty as the backing store for VerticalScale.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty VerticalScaleProperty =
            DependencyProperty.Register(nameof(VerticalScale), typeof(double), typeof(CurveGraph), new PropertyMetadata(50.0, OnVerticalScaleChanged));

        public double HorizontalScale
        {
            get => (double)GetValue(HorizontalScaleProperty);
            set => SetValue(HorizontalScaleProperty, value);
        }

        // Using a DependencyProperty as the backing store for HorizontalScale.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty HorizontalScaleProperty =
            DependencyProperty.Register(nameof(HorizontalScale), typeof(double), typeof(CurveGraph), new PropertyMetadata(50.0, OnHorizontalScaleChanged));

        public double VerticalOffset
        {
            get => (double)GetValue(VerticalOffsetProperty);
            set => SetValue(VerticalOffsetProperty, value);
        }

        // Using a DependencyProperty as the backing store for VerticalOffset.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.Register(nameof(VerticalOffset), typeof(double), typeof(CurveGraph), new PropertyMetadata(0.0, OnVerticalOffsetChanged));

        public double HorizontalOffset
        {
            get => (double)GetValue(HorizontalOffsetProperty);
            set => SetValue(HorizontalOffsetProperty, value);
        }

        // Using a DependencyProperty as the backing store for HorizontalOffset.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty HorizontalOffsetProperty =
            DependencyProperty.Register(nameof(HorizontalOffset), typeof(double), typeof(CurveGraph), new PropertyMetadata(0.0, OnHorizontalOffsetChanged));

        public CurveGraph()
        {
            InitializeComponent();
        }

        private static void OnVerticalScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            
        }

        private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            //throw new NotImplementedException();
        }

        private static void OnHorizontalScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            //throw new NotImplementedException();
        }

        private static void OnHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            //throw new NotImplementedException();
        }

        public double localX(double x)
        {
            return HorizontalScale * (x - HorizontalOffset);
        }

        public double localY(double y)
        {
            return VerticalScale * (y - VerticalOffset);
        }

        public double unrealX(double x)
        {
            return x / HorizontalScale + HorizontalOffset;
        }

        public double unrealY(double y)
        {
            return y / VerticalScale + VerticalOffset;
        }

        public void Paint(bool recomputeView = false)
        {
            TrackLoading = true;
            graph.Children.Clear();

            LinkedList<CurvePoint> points = SelectedCurve.CurvePoints;
            if (points.Count > 0 && recomputeView)
            {
                float timeSpan = points.Last().InVal - points.First().InVal;
                timeSpan = timeSpan > 0 ? timeSpan : 2;
                HorizontalOffset = Math.Round(points.First().InVal - (timeSpan * 0.2));
                double hSpan = Math.Ceiling(timeSpan * 1.2);
                if (hSpan + HorizontalOffset <= timeSpan)
                {
                    hSpan += 1;
                }
                HorizontalScale = graph.ActualWidth / hSpan;
                if (HorizontalOffset >= points.First().InVal - (hSpan / 10))
                {
                    HorizontalOffset = points.First().InVal - (hSpan / 10);
                }
                else if (HorizontalOffset + hSpan <= points.Last().InVal + (hSpan / 10))
                {
                    HorizontalOffset += hSpan / 10;
                }

                float max = points.Max(x => x.OutVal);
                float min = points.Min(x => x.OutVal);
                float valSpan = max - min;
                valSpan = valSpan > 0 ? valSpan : 2;
                VerticalOffset = Math.Round(min - Math.Ceiling(valSpan * 0.1));
                double vSpan = Math.Ceiling(valSpan * 1.2);
                if (vSpan + VerticalOffset <= max)
                {
                    vSpan += 1;
                }
                VerticalScale = graph.ActualHeight / vSpan;
            }

            int numXLines = Convert.ToInt32(Math.Ceiling(ActualWidth / LINE_SPACING));
            int numYLines = Convert.ToInt32(Math.Ceiling(ActualHeight / LINE_SPACING));
            double upperXBound = unrealX(ActualWidth);
            double upperYBound = unrealY(ActualHeight);
            double lineXSpacing = (upperXBound - HorizontalOffset) / numXLines;
            int xGranularity = lineXSpacing > 0.75 ? 1 : (lineXSpacing > 0.25 ? 2 : 10);
            lineXSpacing = Math.Ceiling(lineXSpacing * xGranularity) / xGranularity;
            double lineYSpacing = (upperYBound - VerticalOffset) / numYLines;
            int yGranularity = lineYSpacing > 0.75 ? 1 : (lineYSpacing > 0.25 ? 2 : 10);
            lineYSpacing = Math.Ceiling(lineYSpacing * yGranularity) / yGranularity;

            Line line;
            Label label;
            double linepos;
            for (int i = 0; i < numXLines; i++)
            {
                linepos = HorizontalOffset + (lineXSpacing * (i + 1));
                line = new Line();
                Canvas.SetLeft(line, localX(linepos));
                line.Style = FindResource("VerticalLine") as Style;
                graph.Children.Add(line);

                label = new Label();
                Canvas.SetLeft(label, localX(linepos));
                Canvas.SetBottom(label, 0);
                label.Content = linepos.ToString("0.00");
                graph.Children.Add(label);
            }

            for (int i = 0; i < numYLines; i++)
            {
                linepos = VerticalOffset + (lineYSpacing * (i + 1));
                line = new Line();
                Canvas.SetBottom(line, localY(linepos));
                line.Style = FindResource("HorizontalLine") as Style;
                graph.Children.Add(line);

                label = new Label();
                Canvas.SetBottom(label, localY(linepos));
                label.Content = linepos.ToString("0.00");
                graph.Children.Add(label);
            }

            Anchor lastAnchor = null;
            for (LinkedListNode<CurvePoint> node = points.First; node != null; node = node.Next)
            {
                switch (node.Value.InterpMode)
                {
                    case CurveMode.CIM_CurveAuto:
                    case CurveMode.CIM_CurveUser:
                        node.Value.LeaveTangent = node.Value.ArriveTangent;
                        break;
                    case CurveMode.CIM_CurveAutoClamped:
                        node.Value.ArriveTangent = node.Value.LeaveTangent = 0f;
                        break;
                    case CurveMode.CIM_CurveBreak:
                    case CurveMode.CIM_Constant:
                    case CurveMode.CIM_Linear:
                    default:
                        break;
                }

                Anchor a = new Anchor(this, node);
                if (node.Value == SelectedPoint)
                {
                    a.IsSelected = true;
                }
                graph.Children.Add(a);

                if (node.Previous == null)
                {
                    line = new Line {X1 = -10};
                    line.bind(Line.Y1Property, a, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    line.bind(Line.X2Property, a, nameof(Anchor.X));
                    line.bind(Line.Y2Property, a, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    graph.Children.Add(line);
                }
                else
                {
                    PathBetween(lastAnchor, a, node.Previous.Value.InterpMode);
                }

                if (node.Next == null)
                {
                    line = new Line();
                    line.bind(Line.X1Property, a, nameof(Anchor.X));
                    line.bind(Line.Y1Property, a, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    line.X2 = ActualWidth + 10;
                    line.bind(Line.Y2Property, a, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    graph.Children.Add(line);
                }
                lastAnchor = a;
            }
            TrackLoading = false;
        }

        private void PathBetween(Anchor a1, Anchor a2, CurveMode interpMode = CurveMode.CIM_Linear)
        {
            Line line;
            switch (interpMode)
            {
                case CurveMode.CIM_Linear:
                    line = new Line();
                    line.bind(Line.X1Property, a1, nameof(Anchor.X));
                    line.bind(Line.Y1Property, a1, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    line.bind(Line.X2Property, a2, nameof(Anchor.X));
                    line.bind(Line.Y2Property, a2, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    graph.Children.Add(line);
                    break;
                case CurveMode.CIM_Constant:
                    line = new Line();
                    line.bind(Line.X1Property, a1, nameof(Anchor.X));
                    line.bind(Line.Y1Property, a1, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    line.bind(Line.X2Property, a2, nameof(Anchor.X));
                    line.bind(Line.Y2Property, a1, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    graph.Children.Add(line);
                    line = new Line();
                    line.bind(Line.X1Property, a2, nameof(Anchor.X));
                    line.bind(Line.Y1Property, a1, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    line.bind(Line.X2Property, a2, nameof(Anchor.X));
                    line.bind(Line.Y2Property, a2, nameof(Anchor.Y), new YConverter(), ActualHeight);
                    graph.Children.Add(line);
                    break;
                case CurveMode.CIM_CurveAuto:
                case CurveMode.CIM_CurveUser:
                case CurveMode.CIM_CurveBreak:
                case CurveMode.CIM_CurveAutoClamped:
                    var bez = new BezierSegment(this)
                    {
                        Slope1 = a1.point.Value.LeaveTangent,
                        Slope2 = a2.point.Value.ArriveTangent
                    };
                    bez.bind(BezierSegment.X1Property, a1, nameof(Anchor.X));
                    bez.bind(BezierSegment.Y1Property, a1, nameof(Anchor.Y));
                    bez.bind(BezierSegment.X2Property, a2, nameof(Anchor.X));
                    bez.bind(BezierSegment.Y2Property, a2, nameof(Anchor.Y));
                    graph.Children.Add(bez);
                    a1.rightBez = bez;
                    a2.leftBez = bez;
                    break;
                default:
                    break;
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Paint(true);
        }

        public void invokeSelectedPointChanged()
        {
            SelectedPointChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<CurvePoint>(null, SelectedPoint));
        }

        private void UserControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            VerticalScale *= 1 + ((double)e.Delta / 4000);
            //VerticalOffset += (graph.ActualHeight / VerticalScale) * 0.1 * Math.Sign(e.Delta);
            Paint();
        }

        private void graph_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, graph) && e.RightButton == MouseButtonState.Released)
            {
                dragging = true;
                dragPos = e.GetPosition(graph);
                Cursor = Cursors.ScrollNS;
            }
            else if (ReferenceEquals(e.OriginalSource, graph) && e.ChangedButton == MouseButton.Right)
            {
                ContextMenu cm = new ContextMenu();

                MenuItem addKey = new MenuItem
                {
                    Header = "Add Key",
                    Tag = e.GetPosition(graph)
                };
                addKey.Click += AddKey_Click;
                cm.Items.Add(addKey);

                MenuItem offsetKeys = new MenuItem
                {
                    Header = "Offset All Keys After This Point",
                    Tag = e.GetPosition(graph)
                };
                offsetKeys.Click += OffsetKeys_Click;
                cm.Items.Add(offsetKeys);

                cm.PlacementTarget = sender as Canvas;
                cm.IsOpen = true;
            }
        }

        private void OffsetKeys_Click(object sender, RoutedEventArgs e)
        {
            Point pos = (Point)((MenuItem)sender).Tag;
            double inVal = unrealX(pos.X);
            string res = SharedUI.PromptDialog.Prompt(this, "Seconds to offset keys by", "Curve Editor", "0.0", true);
            if (float.TryParse(res, out var delta))
            {
                LinkedListNode<CurvePoint> node = SelectedCurve.CurvePoints.First;
                while (node?.Next != null && node.Value.InVal < inVal)
                {
                    node = node.Next;
                }
                if (node?.Previous?.Value.InVal - node?.Value.InVal >= delta)
                {
                    MessageBox.Show("Cannot re-order keys");
                }
                else
                {
                    while (node != null)
                    {
                        node.Value.InVal += delta;
                        node = node.Next;
                    }
                    Paint(true); 
                }
            }
        }

        private void AddKey_Click(object sender, RoutedEventArgs e)
        {
            Point pos = (Point)(sender as MenuItem).Tag;
            double inVal = unrealX(pos.X);
            AddKey((float)inVal, (float)unrealY(ActualHeight - pos.Y));
        }

        private void AddKey(float time, float y)
        {
            LinkedListNode<CurvePoint> node;
            try
            {
                node = SelectedCurve.CurvePoints.Find(SelectedCurve.CurvePoints.First(x => x.InVal > time));
                SelectedCurve.AddPoint(new CurvePoint(time, y, 0, 0, node.Value.InterpMode), node);
            }
            catch (Exception)
            {
                node = SelectedCurve.CurvePoints.Last;
                SelectedCurve.AddPoint(new CurvePoint(time, y, 0, 0, node?.Value.InterpMode ?? CurveMode.CIM_CurveUser), node, false);
            }

            Paint(true);
        }

        private void graph_MouseUp(object sender, MouseButtonEventArgs e)
        {
            dragging = false;
            Cursor = Cursors.Arrow;
        }

        private void graph_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point newPos = e.GetPosition(graph);
                double yDiff = newPos.Y - dragPos.Y;
                VerticalOffset += yDiff / VerticalScale;
                Paint();
                dragPos = newPos;
            }
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Anchor a = ((sender as MenuItem).Parent as ContextMenu).Tag as Anchor;
        }

        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox b = sender as TextBox;
            string result;
            if (b.IsSelectionActive)
            {
                result = b.Text.Remove(b.SelectionStart, b.SelectionLength).Insert(b.SelectionStart, e.Text);
            }
            else
            {
                result = b.Text.Insert(b.CaretIndex, e.Text);
            }

            if (!float.TryParse(result, out _))
            {
                e.Handled = true;
            }
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox b = (TextBox)sender;
            //SirCxyrtyx: doing a stack trace to resolve a circular calling situation is horrible, I know. I'm so sorry about this.
            if (double.TryParse(b.Text, out var d) && b.IsFocused && b.IsKeyboardFocused && !FindInStack(nameof(Anchor)))
            {
                Anchor a = graph.Children.OfType<Anchor>().FirstOrDefault(x => x.IsSelected);
                if (a != null && b.Name == nameof(xTextBox))
                {
                    float next = a.point.Next?.Value.InVal ?? float.MaxValue;
                    float prev = a.point.Previous?.Value.InVal ?? float.MinValue;
                    if (d > prev && d < next)
                    {
                        a.X = localX(d);
                        Paint(true);
                    }
                }
                else if (a != null && b.Name == nameof(yTextBox))
                {
                    a.Y = localY(d);
                    Paint(true);
                }
            }
        }
        /// <summary>
        /// Find a string in the stackframes.
        /// </summary>
        /// <param name="FrameName">FrameName to find.</param>
        /// <returns>True if FrameName is found in stack.</returns>
        private static bool FindInStack(string FrameName)
        {
            StackTrace st = new StackTrace();
            for (int i = 0; i < st.FrameCount; i++)
            {
                string name = st.GetFrame(i).GetMethod().ReflectedType.FullName;
                if (name.Contains(FrameName))
                    return true;
            }
            return false;
        }

        public void DeleteSelectedKey()
        {
            LinkedListNode<CurvePoint> point = SelectedCurve.CurvePoints.Find(SelectedPoint);
            if (point != null)
            {
                if (point.Previous != null)
                {
                    SelectedPoint = point.Previous.Value;
                }
                else if (point.Next != null)
                {
                    SelectedPoint = point.Next.Value;
                }
                SelectedCurve.RemovePoint(point);
                Paint();
            }
        }

        public void Clear()
        {
            SelectedCurve = new Curve();
            Paint(true);
            xTextBox.Clear();
            yTextBox.Clear();
        }
    }

    [ValueConversion(typeof(double), typeof(double))]
    public class YConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (double)parameter - (double)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (double)parameter - (double)value;
        }
    }
}
