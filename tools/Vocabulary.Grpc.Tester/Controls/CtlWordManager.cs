using Ruoyu.Study.Vocabulary.Contract.Protos;

namespace Ruoyu.Study.Vocabulary.Test.Controls;

public partial class CtlWordManager : UserControl
{
    private readonly IGrpcClientFactory _grpcClientFactory;
    private readonly VocabularyGrpcService.VocabularyGrpcServiceClient _vocabularyClient;
    private readonly VocabularyBookGrpcService.VocabularyBookGrpcServiceClient _vocabularyBookClient;

    // 定义事件用于与主窗口通信
    public event EventHandler<AddOrUpdateWordEventArgs> OnAddOrUpdateWordRequested = delegate { };
    public event EventHandler OnBooksLoaded = delegate { };

    public CtlWordManager(IGrpcClientFactory grpcClientFactory)
    {
        _grpcClientFactory = grpcClientFactory;
        _vocabularyClient = grpcClientFactory.Get<VocabularyGrpcService.VocabularyGrpcServiceClient>("vocabulary");
        _vocabularyBookClient = grpcClientFactory.Get<VocabularyBookGrpcService.VocabularyBookGrpcServiceClient>("vocabulary");
        
        InitializeComponent();
        this.Load += WordManager_Load;
    }

    private async void WordManager_Load(object sender, EventArgs e)
    {
        await LoadBooksToList();
    }

    private async void btnRefreshBooks_Click(object sender, EventArgs e)
    {
        await LoadBooksToList();
    }

    private async Task LoadBooksToList()
    {
        try
        {
            // 调用gRPC服务获取所有单词本
            var response = await _vocabularyBookClient.GetAllAsync(new Google.Protobuf.WellKnownTypes.Empty());
            
            // 清空下拉框并添加数据
            cmbBookId.Items.Clear();
            foreach (var book in response.Books)
            {
                cmbBookId.Items.Add(new { Id = book.Id, Name = book.BookName });
            }
            
            // 设置显示的文本和值
            cmbBookId.DisplayMember = "Name";
            cmbBookId.ValueMember = "Id";
            
            // 默认选中第一个单词本
            if (cmbBookId.Items.Count > 0)
            {
                cmbBookId.SelectedIndex = 0;
            }
            
            // 触发单词本加载完成事件
            OnBooksLoaded?.Invoke(this, EventArgs.Empty);
        } 
        catch (Exception ex)
        {
            MessageBox.Show($"加载单词本列表失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void btnAddOrUpdateWord_Click(object sender, EventArgs e)
    {
        try
        {
            // 验证必填字段
            if (string.IsNullOrWhiteSpace(txtWord.Text))
            {
                MessageBox.Show("请输入单词拼写", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtMeaning.Text))
            {
                MessageBox.Show("请输入单词词义", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtPartOfSpeech.Text))
            {
                MessageBox.Show("请输入单词词性", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbBookId.SelectedItem == null)
            {
                MessageBox.Show("请选择单词本", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 获取选中的单词本ID
            var selectedBook = cmbBookId.SelectedItem as dynamic;
            var bookId = selectedBook.Id;

            // 创建请求对象
            var request = new AddOrUpdateRequest
            {
                Word = new VocabularyDto
                {
                    Word = txtWord.Text.Trim(),
                    Phonetic = txtPhonetic.Text.Trim()
                },
                Meaning = new VocabularyMeaningDto
                {
                    BookId = bookId,
                    PartOfSpeech = txtPartOfSpeech.Text.Trim(),
                    Meaning = txtMeaning.Text.Trim(),
                    Example = txtExample.Text.Trim()
                }
            };

            // 触发添加/修改单词事件
            OnAddOrUpdateWordRequested?.Invoke(this, new AddOrUpdateWordEventArgs(request));

        } 
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 公共方法，用于清空表单
    public void ClearForm()
    {
        txtWord.Text = string.Empty;
        txtPhonetic.Text = string.Empty;
        txtPartOfSpeech.Text = string.Empty;
        txtMeaning.Text = string.Empty;
        txtExample.Text = string.Empty;
        if (cmbBookId.Items.Count > 0)
        {
            cmbBookId.SelectedIndex = 0;
        }
    }
}

// 添加/修改单词事件参数类
public class AddOrUpdateWordEventArgs : EventArgs
{
    public AddOrUpdateRequest Request { get; }

    public AddOrUpdateWordEventArgs(AddOrUpdateRequest request)
    {
        Request = request;
    }
}