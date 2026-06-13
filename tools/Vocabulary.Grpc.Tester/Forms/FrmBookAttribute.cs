using Ruoyu.Study.Vocabulary.Contract.Protos;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;

namespace Ruoyu.Study.Vocabulary.Test.Forms
{
    public partial class FrmBookAttribute : Form
    {
        private readonly VocabularyBookGrpcService.VocabularyBookGrpcServiceClient _client;
        
        public VocabularyBookDto Book { get; private set; }
        public bool IsConfirmed { get; private set; }
        private bool _isEditMode = false;

        public FrmBookAttribute() : this(null)
        {
        }

        public FrmBookAttribute(IGrpcClientFactory grpcClientFactory)
        {
            // 如果提供了gRPC客户端工厂，则创建客户端
            if (grpcClientFactory != null)
            {
                _client = grpcClientFactory.Get<VocabularyBookGrpcService.VocabularyBookGrpcServiceClient>("vocabulary");
            }
            
            InitializeComponent();
            LoadComboBoxData();
        }

        public FrmBookAttribute(IGrpcClientFactory grpcClientFactory, VocabularyBookDto existingBook)
        {
            // 如果提供了gRPC客户端工厂，则创建客户端
            if (grpcClientFactory != null)
            {
                _client = grpcClientFactory.Get<VocabularyBookGrpcService.VocabularyBookGrpcServiceClient>("vocabulary");
            }
            
            InitializeComponent();
            _isEditMode = true;
            Book = existingBook;
            Text = "编辑单词本";
            btnSave.Text = "保存修改";
            LoadComboBoxData();
        }

        private async void LoadComboBoxData()
        {
            // 加载状态下拉框数据（状态是固定的布尔值映射）
            cmbStatus.Items.AddRange(new[] { "启用", "禁用" });
            cmbStatus.SelectedIndex = 0;

            // 如果有gRPC客户端，从服务端获取下拉框数据
            if (_client != null)
            {
                try
                {
                    // 加载分类下拉框数据
                    var categoriesResponse = await _client.GetAllCategoriesAsync(new Empty());
                    cmbCategory.Items.Clear();
                    cmbCategory.Items.AddRange(categoriesResponse.Items.ToArray());
                    if (cmbCategory.Items.Count > 0)
                        cmbCategory.SelectedIndex = 0;

                    // 加载教育阶段下拉框数据
                    var educationLevelsResponse = await _client.GetAllEducationLevelsAsync(new Empty());
                    cmbEducationLevel.Items.Clear();
                    cmbEducationLevel.Items.AddRange(educationLevelsResponse.Items.ToArray());
                    if (cmbEducationLevel.Items.Count > 0)
                        cmbEducationLevel.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载下拉框数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // 使用默认数据
                    LoadDefaultComboBoxData();
                }
            }
            else
            {
                // 使用默认数据
                LoadDefaultComboBoxData();
            }

            // 初始化年级下拉框
            await UpdateGradeComboBoxAsync();

            // 如果是编辑模式，设置表单初始值
            if (_isEditMode && Book != null)
            {
                txtBookName.Text = Book.BookName;
                txtDescription.Text = Book.Description;
                cmbStatus.SelectedItem = Book.Status ? "启用" : "禁用";
                
                // 设置分类、教育阶段和年级
                cmbCategory.SelectedItem = cmbCategory.Items.Contains(Book.Category) ? Book.Category : null;
                cmbEducationLevel.SelectedItem = cmbEducationLevel.Items.Contains(Book.EducationLevel) ? Book.EducationLevel : null;
                cmbGrade.SelectedItem = cmbGrade.Items.Contains(Book.Grade) ? Book.Grade : null;
            }
        }

        private void LoadDefaultComboBoxData()
        {
            // 加载默认分类下拉框数据
            cmbCategory.Items.Clear();
            cmbCategory.Items.AddRange(new[] { "基础词汇", "进阶词汇", "专业词汇", "考试词汇", "其他" });
            cmbCategory.SelectedIndex = 0;

            // 加载默认教育阶段下拉框数据
            cmbEducationLevel.Items.Clear();
            cmbEducationLevel.Items.AddRange(new[] { "小学", "初中", "高中", "大学", "成人教育", "其他" });
            cmbEducationLevel.SelectedIndex = 0;
        }

        private async Task UpdateGradeComboBoxAsync()
        {
            cmbGrade.Items.Clear();
            string educationLevel = cmbEducationLevel.SelectedItem?.ToString() ?? "";

            // 如果有gRPC客户端，从服务端获取年级数据
            if (_client != null && !string.IsNullOrEmpty(educationLevel))
            {
                try
                {
                    var gradesResponse = await _client.GetGradesByEducationLevelAsync(new StringRequest { Value = educationLevel });
                    cmbGrade.Items.AddRange(gradesResponse.Items.ToArray());
                    if (cmbGrade.Items.Count > 0)
                        cmbGrade.SelectedIndex = 0;
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载年级数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // 使用默认数据
                }
            }

            // 使用默认年级数据
            switch (educationLevel)
            {
                case "小学":
                    cmbGrade.Items.AddRange(new[] { "一年级", "二年级", "三年级", "四年级", "五年级", "六年级" });
                    break;
                case "初中":
                    cmbGrade.Items.AddRange(new[] { "初一", "初二", "初三" });
                    break;
                case "高中":
                    cmbGrade.Items.AddRange(new[] { "高一", "高二", "高三" });
                    break;
                case "大学":
                    cmbGrade.Items.AddRange(new[] { "大一", "大二", "大三", "大四", "研究生", "博士生" });
                    break;
                case "成人教育":
                case "其他":
                    cmbGrade.Items.AddRange(new[] { "初级", "中级", "高级" });
                    break;
                default:
                    cmbGrade.Items.AddRange(new[] { "一年级", "二年级", "三年级", "四年级", "五年级", "六年级" });
                    break;
            }

            if (cmbGrade.Items.Count > 0)
                cmbGrade.SelectedIndex = 0;
        }

        private async void CmbEducationLevel_SelectedIndexChanged(object? sender, EventArgs e)
        {
            await UpdateGradeComboBoxAsync();
        }

        

        private async void BtnSave_Click(object? sender, EventArgs e)
        {
            if (ValidateInput())
            {
                Book = new VocabularyBookDto
                {
                    Id = _isEditMode ? Book.Id : Guid.NewGuid().ToString(),
                    BookName = txtBookName.Text.Trim(),
                    Description = txtDescription.Text.Trim(),
                    Category = cmbCategory.SelectedItem?.ToString() ?? "基础词汇",
                    EducationLevel = cmbEducationLevel.SelectedItem?.ToString() ?? "小学",
                    Grade = cmbGrade.SelectedItem?.ToString() ?? "一年级",
                    Publisher = _isEditMode ? Book.Publisher : "",
                    DisplayOrder = _isEditMode ? Book.DisplayOrder : 0,
                    IconUrl = _isEditMode ? Book.IconUrl : "",
                    Status = cmbStatus.SelectedItem?.ToString() == "启用"
                };

                // 默认认为操作是成功的
                bool operationSuccess = true;
                string errorMessage = "";

                // 如果有gRPC客户端，则调用远程服务添加或更新单词本
                if (_client != null)
                {
                    try
                    {
                        BoolResponse response;
                        if (_isEditMode)
                        {
                            response = await _client.UpdateAsync(Book);
                        }
                        else
                        {
                            response = await _client.AddAsync(Book);
                        }

                        if (!response.Success)
                        {
                            operationSuccess = false;
                            errorMessage = response.ErrorMessage;
                        }
                    }
                    catch (Exception ex)
                    {
                        operationSuccess = false;
                        errorMessage = ex.Message;
                    }
                }
                else
                {
                    // 如果客户端未初始化，显示错误信息
                    operationSuccess = false;
                    errorMessage = _isEditMode ? "gRPC客户端未初始化，无法更新单词本" : "gRPC客户端未初始化，无法添加单词本";
                }

                // 根据操作结果进行处理
                if (operationSuccess)
                {
                    // 显示具体的操作结果
                    string operationType = _isEditMode ? "修改" : "添加";
                    string resultMessage = $"单词本{operationType}成功！\n\n" +
                                         $"ID: {Book.Id}\n" +
                                         $"名称: {Book.BookName}\n" +
                                         $"分类: {Book.Category}\n" +
                                         $"教育阶段: {Book.EducationLevel}\n" +
                                         $"年级: {Book.Grade}\n" +
                                         $"状态: {(Book.Status ? "启用" : "禁用")}\n" +
                                         $"描述: {Book.Description}";
                    
                    MessageBox.Show(resultMessage, $"{operationType}成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    
                    IsConfirmed = true;
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    MessageBox.Show($"{(_isEditMode ? "更新" : "添加")}单词本失败：{errorMessage}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            IsConfirmed = false;
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(txtBookName.Text))
            {
                MessageBox.Show("请输入单词本名称！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtBookName.Focus();
                return false;
            }

            if (txtBookName.Text.Length > 100)
            {
                MessageBox.Show("单词本名称不能超过100个字符！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtBookName.Focus();
                return false;
            }

            if (txtDescription.Text.Length > 500)
            {
                MessageBox.Show("描述不能超过500个字符！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtDescription.Focus();
                return false;
            }

            return true;
        }
    }
}