using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Test.Forms;

namespace Ruoyu.Study.Vocabulary.Test.Controls;

public partial class CtlBookManager : UserControl
{
    private int currentPage = 1;
    private int pageSize = 20;
    private int totalRecords = 0;
    private List<VocabularyBookDto> books = new List<VocabularyBookDto>();

    private readonly IGrpcClientFactory _grpcClientFactory;

    // 定义事件用于与主窗口通信
    public event EventHandler OnRefreshRequested = delegate { };
    public event EventHandler<SearchBooksEventArgs> OnSearchBooksRequested = delegate { };
    public event EventHandler<AddBookEventArgs> OnAddBookRequested = delegate { };
    public event EventHandler<AddBookEventArgs> OnAddBookConfirmed = delegate { };

    public CtlBookManager(IGrpcClientFactory grpcClientFactory = null)
    {
        _grpcClientFactory = grpcClientFactory;
        InitializeComponent();
        // 初始化分页大小下拉框
        InitializePageSizeComboBox();
        // 延迟加载数据，等主窗口连接事件后再触发
        this.Load += BookManager_Load;
        // 添加DataGridView双击事件处理
        dgvBooks.CellDoubleClick += DgvBooks_CellDoubleClick;
    }

    private void InitializePageSizeComboBox()
    {
        if (cmbPageSize != null)
        {
            // 设置默认选项
            cmbPageSize.Items.Clear();
            cmbPageSize.Items.AddRange(new object[] { "10", "20", "50", "100" });

            // 设置默认选中项为20
            switch (pageSize)
            {
                case 10:
                    cmbPageSize.SelectedIndex = 0;
                    break;
                case 20:
                    cmbPageSize.SelectedIndex = 1;
                    break;
                case 50:
                    cmbPageSize.SelectedIndex = 2;
                    break;
                case 100:
                    cmbPageSize.SelectedIndex = 3;
                    break;
                default:
                    cmbPageSize.SelectedIndex = 1; // 默认选中20
                    break;
            }
        }
    }

    private void BookManager_Load(object sender, EventArgs e)
    {
        LoadBooks();
    }

    private async void LoadBooks()
    {
        try
        {
            // 触发刷新事件，让主窗口处理数据加载
            OnRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateDataGridView()
    {
        var pageBooks = GetPageData();
        dgvBooks.DataSource = null;
        dgvBooks.DataSource = pageBooks;
    }

    private List<VocabularyBookDto> GetPageData()
    {
        int startIndex = (currentPage - 1) * pageSize;
        int endIndex = Math.Min(startIndex + pageSize, books.Count);

        return books.GetRange(startIndex, endIndex - startIndex);
    }

    private void UpdatePageInfo()
    {
        int totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);
        lblPageInfo.Text = $"第 {currentPage} 页，共 {totalPages} 页，共 {totalRecords} 条记录";
    }

    private void UpdatePaginationButtons()
    {
        int totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);

        btnFirst.Enabled = currentPage > 1;
        btnPrevious.Enabled = currentPage > 1;
        btnNext.Enabled = currentPage < totalPages;
        btnLast.Enabled = currentPage < totalPages;
    }

    private void BtnFirst_Click(object sender, EventArgs e)
    {
        if (currentPage != 1)
        {
            currentPage = 1;
            UpdateDataGridView();
            UpdatePageInfo();
            UpdatePaginationButtons();
        }
    }

    private void BtnPrevious_Click(object sender, EventArgs e)
    {
        if (currentPage > 1)
        {
            currentPage--;
            UpdateDataGridView();
            UpdatePageInfo();
            UpdatePaginationButtons();
        }
    }

    private void BtnNext_Click(object sender, EventArgs e)
    {
        int totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);
        if (currentPage < totalPages)
        {
            currentPage++;
            UpdateDataGridView();
            UpdatePageInfo();
            UpdatePaginationButtons();
        }
    }

    private void BtnLast_Click(object sender, EventArgs e)
    {
        int totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);
        if (currentPage != totalPages)
        {
            currentPage = totalPages;
            UpdateDataGridView();
            UpdatePageInfo();
            UpdatePaginationButtons();
        }
    }

    private void BtnRefresh_Click(object sender, EventArgs e)
    {
        LoadBooks();
    }

    private void BtnAddBook_Click(object sender, EventArgs e)
    {
        using (var addDialog = new FrmBookAttribute(_grpcClientFactory))
        {
            if (addDialog.ShowDialog() == DialogResult.OK && addDialog.IsConfirmed)
            {
                // 触发添加单词本事件
                OnAddBookRequested?.Invoke(this, new AddBookEventArgs(addDialog.Book));
                OnAddBookConfirmed?.Invoke(this, new AddBookEventArgs(addDialog.Book));
                // 添加成功后自动刷新列表
                LoadBooks();
            }
        }
    }

    private void DgvBooks_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
    {
        // 确保点击的是有效行
        if (e.RowIndex >= 0 && dgvBooks.Rows[e.RowIndex].DataBoundItem is VocabularyBookDto book)
        {
            // 打开编辑窗体
            using (var editDialog = new FrmBookAttribute(_grpcClientFactory, book))
            {
                if (editDialog.ShowDialog() == DialogResult.OK && editDialog.IsConfirmed)
                {
                    // 触发添加或更新事件（这里可以考虑添加单独的更新事件）
                    OnAddBookConfirmed?.Invoke(this, new AddBookEventArgs(editDialog.Book));
                    // 更新成功后自动刷新列表
                    LoadBooks();
                }
            }
        }
    }

    private void cmbPageSize_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (cmbPageSize?.SelectedItem is string sizeText && int.TryParse(sizeText, out int size))
        {
            // 更新页大小
            pageSize = size;
            // 重置到第一页
            currentPage = 1;
            // 重新加载数据
            LoadBooks();
        }
    }

    // 公共方法，供主窗口调用来设置数据
    public void SetBooksData(List<VocabularyBookDto> bookList, int totalCount)
    {
        books = bookList ?? new List<VocabularyBookDto>();
        totalRecords = totalCount;
        currentPage = 1; // 重置到第一页

        UpdateDataGridView();
        UpdatePageInfo();
        UpdatePaginationButtons();
    }

    // 公共方法，供主窗口调用来添加搜索功能
    public void SearchBooks(string keyword)
    {
        OnSearchBooksRequested?.Invoke(this, new SearchBooksEventArgs(keyword, currentPage, pageSize));
    }

    // 删除按钮点击事件
    private async void BtnDeleteBook_Click(object sender, EventArgs e)
    {
        // 检查是否有选中的行
        if (dgvBooks.SelectedRows.Count == 0)
        {
            MessageBox.Show("请先选择要删除的单词本", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 获取选中的行
        var selectedRow = dgvBooks.SelectedRows[0];
        if (selectedRow.DataBoundItem is VocabularyBookDto book)
        {
            // 显示确认对话框
            var result = MessageBox.Show(
                $"确定要删除单词本 '{book.BookName}' 吗？",
                "删除确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    // 调用gRPC服务删除单词本
                    var client = _grpcClientFactory.Get<VocabularyBookGrpcService.VocabularyBookGrpcServiceClient>("vocabulary");
                    var response = await client.DeleteAsync(new IdRequest { Id = book.Id });

                    if (response.Success)
                    {
                        MessageBox.Show("删除成功", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        // 删除成功后自动刷新列表
                        LoadBooks();
                    }
                    else
                    {
                        MessageBox.Show($"删除失败: {response.ErrorMessage}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}

// 搜索事件参数类
public class SearchBooksEventArgs : EventArgs
{
    public string Keyword { get; }
    public int Page { get; }
    public int Size { get; }

    public SearchBooksEventArgs(string keyword, int page, int size)
    {
        Keyword = keyword;
        Page = page;
        Size = size;
    }
}

// 添加单词本事件参数类
public class AddBookEventArgs : EventArgs
{
    public VocabularyBookDto Book { get; }

    public AddBookEventArgs(VocabularyBookDto book)
    {
        Book = book;
    }
}