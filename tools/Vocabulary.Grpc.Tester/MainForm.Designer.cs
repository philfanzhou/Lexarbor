namespace Ruoyu.Study.Grpc.Vocabulary.Test;

partial class MainForm
{
    /// <summary>
    /// 必需的设计器变量。
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// 清理所有正在使用的资源。
    /// </summary>
    /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows 窗体设计器生成的代码

    /// <summary>
    /// 设计器支持所需的方法 - 不要修改
    /// 使用代码编辑器修改此方法的内容。
    /// </summary>
    private void InitializeComponent()
    {
        tabControl1 = new TabControl();
        tabPage1 = new TabPage();
        tabPageResult = new TabPage();
        label1 = new Label();
        txtStatus = new TextBox();
        rtbResult = new RichTextBox();
        tabPage2 = new TabPage();
        tabControl1.SuspendLayout();
        tabPageResult.SuspendLayout();
        tabPage2.SuspendLayout();
        SuspendLayout();
        // 
        // tabControl1
        // 
        tabControl1.Controls.Add(tabPage1);
        tabControl1.Controls.Add(tabPage2);
        tabControl1.Controls.Add(tabPageResult);
        tabControl1.Dock = DockStyle.Fill;
        tabControl1.Location = new Point(0, 0);
        tabControl1.Margin = new Padding(5);
        tabControl1.Name = "tabControl1";
        tabControl1.SelectedIndex = 0;
        tabControl1.Size = new Size(1200, 542);
        tabControl1.TabIndex = 0;
        // 
        // tabPage1
        // 
        tabPage1.Location = new Point(4, 29);
        tabPage1.Margin = new Padding(5);
        tabPage1.Name = "tabPage1";
        tabPage1.Padding = new Padding(5);
        tabPage1.Size = new Size(1192, 509);
        tabPage1.TabIndex = 3;
        tabPage1.Text = "单词本管理";
        tabPage1.UseVisualStyleBackColor = true;
        // 
        // tabPageResult
        // 
        tabPageResult.Controls.Add(rtbResult);
        tabPageResult.Location = new Point(4, 29);
        tabPageResult.Margin = new Padding(5);
        tabPageResult.Name = "tabPageResult";
        tabPageResult.Padding = new Padding(5);
        tabPageResult.Size = new Size(1192, 509);
        tabPageResult.TabIndex = 4;
        tabPageResult.Text = "操作结果";
        tabPageResult.UseVisualStyleBackColor = true;
        // 
        // tabPage2
        // 
        tabPage2.Location = new Point(4, 29);
        tabPage2.Margin = new Padding(5);
        tabPage2.Name = "tabPage2";
        tabPage2.Padding = new Padding(5);
        tabPage2.Size = new Size(1192, 509);
        tabPage2.TabIndex = 2;
        tabPage2.Text = "单词添加/修改";
        tabPage2.UseVisualStyleBackColor = true;
        // 
        // rtbResult
        // 
        rtbResult.Dock = DockStyle.Fill;
        rtbResult.Font = new Font("Consolas", 10F);
        rtbResult.Location = new Point(5, 5);
        rtbResult.Name = "rtbResult";
        rtbResult.ReadOnly = true;
        rtbResult.ScrollBars = RichTextBoxScrollBars.Both;
        rtbResult.Size = new Size(1182, 499);
        rtbResult.TabIndex = 0;
        rtbResult.Text = "";
        rtbResult.WordWrap = false;
        // 
        // label1
        // 
        label1.Dock = DockStyle.Bottom;
        label1.Location = new Point(0, 569);
        label1.Margin = new Padding(5, 0, 5, 0);
        label1.Name = "label1";
        label1.Size = new Size(1200, 20);
        label1.TabIndex = 1;
        label1.Text = "状态：";
        // 
        // txtStatus
        // 
        txtStatus.Dock = DockStyle.Bottom;
        txtStatus.Location = new Point(0, 542);
        txtStatus.Margin = new Padding(5);
        txtStatus.Name = "txtStatus";
        txtStatus.ReadOnly = true;
        txtStatus.Size = new Size(1200, 27);
        txtStatus.TabIndex = 2;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(9F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1200, 589);
        Controls.Add(tabControl1);
        Controls.Add(txtStatus);
        Controls.Add(label1);
        Margin = new Padding(5);
        Name = "MainForm";
        Text = "词汇服务测试程序";
        tabControl1.ResumeLayout(false);
        tabPageResult.ResumeLayout(false);
        tabPage2.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private System.Windows.Forms.TabControl tabControl1;
    private System.Windows.Forms.TabPage tabPage1;
    private System.Windows.Forms.TabPage tabPageResult;
    private System.Windows.Forms.Label label1;
    private System.Windows.Forms.TextBox txtStatus;
    private System.Windows.Forms.RichTextBox rtbResult;
    private System.Windows.Forms.TabPage tabPage2;
}