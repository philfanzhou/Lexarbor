﻿using Microsoft.Extensions.Logging;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Test;
using Ruoyu.Study.Vocabulary.Test.Controls;

namespace Ruoyu.Study.Grpc.Vocabulary.Test;

public partial class MainForm : Form
{
    private readonly VocabularyGrpcService.VocabularyGrpcServiceClient _vocabularyClient;
    private readonly VocabularyBookGrpcService.VocabularyBookGrpcServiceClient _vocabularyBookClient;
    private readonly ILogger<MainForm>? _logger;
    private readonly IGrpcClientFactory _grpcClientFactory;

    // 结果显示相关字段
    private CtlBookManager? bookManager;
    private CtlWordManager? wordManager;

    public MainForm(
        VocabularyGrpcService.VocabularyGrpcServiceClient vocabularyClient,
        VocabularyBookGrpcService.VocabularyBookGrpcServiceClient vocabularyBookClient,
        ILogger<MainForm> logger,
        IGrpcClientFactory grpcClientFactory)
    {
        // 先赋值，确保_logger不为null
        _vocabularyClient = vocabularyClient;
        _vocabularyBookClient = vocabularyBookClient;
        _logger = logger;
        _grpcClientFactory = grpcClientFactory;

        try
        {
            _logger?.LogInformation("开始InitializeComponent");
            InitializeComponent();

            _logger?.LogInformation("开始初始化用户控件");
            // 初始化用户控件
            InitializeUserControls();

            txtStatus.Text = "客户端初始化成功";
            _logger?.LogInformation("gRPC clients initialized successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "初始化MainForm时发生错误");
            // 输出到控制台以便在终端中看到
            Console.WriteLine($"初始化错误: {ex.Message}");
            Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            throw; // 重新抛出异常
        }
    }

    private void InitializeUserControls()
    {
        // 初始化新的BookManager控件
        bookManager = new CtlBookManager(_grpcClientFactory);

        // 连接BookManager的事件到对应的处理方法
        bookManager.OnRefreshRequested += async (sender, e) => await HandleBookManagerRefreshAsync();
        bookManager.OnSearchBooksRequested += async (sender, e) => await HandleBookManagerSearchAsync(e.Keyword, e.Page, e.Size);

        // 初始化单词本管理页面布局
        InitializeBookManagerLayout();

        // 初始化新的WordManager控件
        wordManager = new CtlWordManager(_grpcClientFactory);

        // 连接WordManager的事件到对应的处理方法
        wordManager.OnAddOrUpdateWordRequested += async (sender, e) => 
        {
            await HandleWordManagerAddOrUpdateAsync(e.Request);
        };
        wordManager.OnBooksLoaded += (sender, e) => ShowStatus("单词本列表加载完成");

        // 初始化单词管理页面布局
        InitializeWordManagerLayout();
    }

    private void InitializeBookManagerLayout()
    {
        // 将BookManager控件添加到tabPage1
        bookManager.Dock = DockStyle.Fill;
        tabPage1.Controls.Add(bookManager);
    }

    private void InitializeWordManagerLayout()
    {
        // 将WordManager控件添加到tabPage2
        wordManager.Dock = DockStyle.Fill;
        tabPage2.Controls.Add(wordManager);
    }

    private async Task HandleWordManagerAddOrUpdateAsync(AddOrUpdateRequest request)
    {
        try
        {
            ShowStatus("正在添加/修改单词...");

            // 调用gRPC服务
            var result = await _vocabularyClient.AddOrUpdateAsync(request);

            // 显示结果
            rtbResult.Clear();
            if (result.Success)
            {
                rtbResult.SelectionColor = Color.Green;
                rtbResult.AppendText($"操作成功！\n\n");
                rtbResult.SelectionColor = Color.Black;
                rtbResult.AppendText($"单词: {request.Word.Word}\n");
                if (!string.IsNullOrWhiteSpace(request.Word.Phonetic))
                {
                    rtbResult.AppendText($"音标: {request.Word.Phonetic}\n");
                }
                rtbResult.AppendText($"词性: {request.Meaning.PartOfSpeech}\n");
                rtbResult.AppendText($"词义: {request.Meaning.Meaning}\n");
                if (!string.IsNullOrWhiteSpace(request.Meaning.Example))
                {
                    rtbResult.AppendText($"例句: {request.Meaning.Example}\n");
                }
                
                // 清空表单
                wordManager?.ClearForm();
            }
            else
            {
                rtbResult.SelectionColor = Color.Red;
                rtbResult.AppendText($"操作失败！\n");
                rtbResult.SelectionColor = Color.Black;
                rtbResult.AppendText($"服务返回失败状态: {result.ErrorMessage}");
            }

            if (result.Success)
            {
                ShowStatus("单词添加/修改成功");
            }
            else
            {
                ShowStatus($"单词添加/修改失败: {result.ErrorMessage}");
            }

            // 切换到结果标签页
            tabControl1.SelectedTab = tabPageResult;

        }
        catch (Exception ex)
        {
            ShowStatus($"操作失败: {ex.Message}");
            rtbResult.Clear();
            rtbResult.SelectionColor = Color.Red;
            rtbResult.AppendText($"操作失败: {ex.Message}\n\n");
            rtbResult.AppendText($"堆栈跟踪: {ex.StackTrace}");
            tabControl1.SelectedTab = tabPageResult;
        }
    }

    #region 单词本管理事件处理

    private async Task HandleBookManagerRefreshAsync()
    {
        try
        {
            ShowStatus("正在刷新单词本数据...");

            // 获取所有单词本数据
            var result = await _vocabularyBookClient.GetAllAsync(new());

            // 设置数据到BookManager控件
            if (bookManager != null)
            {
                bookManager.SetBooksData(result.Books.ToList(), result.Books.Count);
            }

            ShowStatus("单词本数据刷新成功");
        }
        catch (Exception ex)
        {
            ShowStatus($"刷新单词本数据失败: {ex.Message}");
            MessageBox.Show($"刷新数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task HandleBookManagerSearchAsync(string keyword, int page, int size)
    {
        try
        {
            ShowStatus($"正在搜索单词本: {keyword}");

            // 搜索单词本
            var result = await _vocabularyBookClient.SearchAsync(new()
            {
                Keyword = keyword,
                Page = page,
                Size = size
            });

            // 设置数据到BookManager控件
            if (bookManager != null)
            {
                bookManager.SetBooksData(result.Items.ToList(), result.TotalCount);
            }

            ShowStatus($"搜索完成，找到 {result.TotalCount} 条记录");
        }
        catch (Exception ex)
        {
            ShowStatus($"搜索单词本失败: {ex.Message}");
            MessageBox.Show($"搜索失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    #endregion

    #region 辅助方法

    private void ShowStatus(string status)
    {
        txtStatus.Text = status;
        _logger?.LogInformation(status);

        // 同时在结果区域显示状态信息
        if (rtbResult != null && !rtbResult.IsDisposed)
        {
            rtbResult.SelectionColor = Color.Blue;
            rtbResult.AppendText($"\n[状态] {status}");
            rtbResult.ScrollToCaret();
        }
    }

    #endregion
}