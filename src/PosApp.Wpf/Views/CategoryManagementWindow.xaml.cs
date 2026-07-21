using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Entities;
using PosApp.Core.Interfaces;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class CategoryManagementWindow : Window
{
    private readonly IInventoryService _inventory;
    private readonly ObservableCollection<Category> _categories = new();
    private int? _editingId;
    private bool _loading;

    public CategoryManagementWindow(IInventoryService inventory)
    {
        InitializeComponent();
        _inventory = inventory;
        CategoriesGrid.ItemsSource = _categories;
        Loaded += CategoryManagementWindow_Loaded;
        ResetEditor();
    }

    private async void CategoryManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception exception)
        {
            LocalizedMessageBox.Show(this, exception.GetBaseException().Message,
                DialogLayout.Text("Cat_LoadErrorTitle", "Unable to load categories"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadAsync(int? selectId = null)
    {
        Category? selected = null;
        _loading = true;
        try
        {
            var categories = await _inventory.ListCategoriesAsync();
            _categories.Clear();
            foreach (var category in categories)
                _categories.Add(category);

            selected = selectId.HasValue
                ? _categories.FirstOrDefault(category => category.Id == selectId.Value)
                : null;
            CategoriesGrid.SelectedItem = selected;
            if (selected != null)
                CategoriesGrid.ScrollIntoView(selected);
        }
        finally
        {
            _loading = false;
        }

        if (selected != null)
            PopulateEditor(selected);
        else
            ResetEditor();
    }

    private void CategoriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoriesGrid.SelectedItem is not Category category)
            return;

        PopulateEditor(category);
    }

    private void PopulateEditor(Category category)
    {
        _editingId = category.Id;
        EditorTitle.Text = DialogLayout.Text("Cat_EditorEdit", "Edit category");
        NameBox.Text = category.Name;
        DescriptionBox.Text = category.Description ?? string.Empty;
        ColorBox.Text = category.Color;
        SortOrderBox.Text = category.SortOrder.ToString(CultureInfo.InvariantCulture);
        DeleteButton.IsEnabled = true;
        StatusText.Text = string.Empty;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        CategoriesGrid.SelectedItem = null;
        ResetEditor();
        NameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("Cat_NameRequired", "Enter a category name.", NameBox);
            return;
        }
        if (!int.TryParse(SortOrderBox.Text.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var sortOrder))
        {
            ShowValidation("Cat_SortOrderInvalid", "Sort order must be a whole number.", SortOrderBox);
            return;
        }

        var category = new Category
        {
            Id = _editingId ?? 0,
            Name = name,
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text)
                ? null
                : DescriptionBox.Text.Trim(),
            Color = ColorBox.Text.Trim(),
            SortOrder = sortOrder,
            IsActive = true
        };

        try
        {
            IsEnabled = false;
            var saved = await _inventory.CreateOrUpdateCategoryAsync(category);
            await LoadAsync(saved.Id);
            StatusText.Text = DialogLayout.Text("Cat_Saved", "Category saved and queued for synchronization.");
        }
        catch (Exception exception)
        {
            LocalizedMessageBox.Show(this, exception.GetBaseException().Message,
                DialogLayout.Text("Cat_SaveErrorTitle", "Unable to save category"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is not int categoryId ||
            CategoriesGrid.SelectedItem is not Category category)
            return;

        var confirmation = string.Format(
            CultureInfo.CurrentCulture,
            DialogLayout.Text("Cat_DeleteConfirm", "Delete category '{0}'?"),
            category.Name);
        if (LocalizedMessageBox.Show(this, confirmation,
                DialogLayout.Text("Cat_DeleteTitle", "Delete category"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            IsEnabled = false;
            await _inventory.DeleteCategoryAsync(categoryId);
            await LoadAsync();
            StatusText.Text = DialogLayout.Text("Cat_Deleted", "Category deleted and queued for synchronization.");
        }
        catch (Exception exception)
        {
            LocalizedMessageBox.Show(this, exception.GetBaseException().Message,
                DialogLayout.Text("Cat_DeleteErrorTitle", "Unable to delete category"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ResetEditor()
    {
        _editingId = null;
        if (EditorTitle == null)
            return;

        EditorTitle.Text = DialogLayout.Text("Cat_EditorNew", "New category");
        NameBox.Clear();
        DescriptionBox.Clear();
        ColorBox.Text = "#2D7FF9";
        SortOrderBox.Text = (_categories.Count == 0
                ? 1
                : _categories.Max(category => category.SortOrder) + 1)
            .ToString(CultureInfo.InvariantCulture);
        DeleteButton.IsEnabled = false;
        StatusText.Text = string.Empty;
    }

    private void ShowValidation(string key, string fallback, Control target)
    {
        LocalizedMessageBox.Show(this, DialogLayout.Text(key, fallback),
            DialogLayout.Text("Cat_InvalidTitle", "Check category details"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        target.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
