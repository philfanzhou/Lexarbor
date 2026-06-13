namespace Ruoyu.Study.Vocabulary.Test.Controls;

partial class CtlWordManager
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Component Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify 
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        tableLayoutPanel1 = new TableLayoutPanel();
        label13 = new Label();
        panelBookControls = new TableLayoutPanel();
        btnRefreshBooks = new Button();
        cmbBookId = new ComboBox();
        label8 = new Label();
        txtWord = new TextBox();
        label9 = new Label();
        txtPhonetic = new TextBox();
        label10 = new Label();
        txtPartOfSpeech = new TextBox();
        label11 = new Label();
        txtMeaning = new TextBox();
        label12 = new Label();
        txtExample = new TextBox();
        btnAddOrUpdateWord = new Button();
        tableLayoutPanel1.SuspendLayout();
        panelBookControls.SuspendLayout();
        SuspendLayout();
        // 
        // tableLayoutPanel1
        // 
        tableLayoutPanel1.ColumnCount = 2;
        tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 147F));
        tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tableLayoutPanel1.Controls.Add(label13, 0, 0);
        tableLayoutPanel1.Controls.Add(panelBookControls, 1, 0);
        tableLayoutPanel1.Controls.Add(label8, 0, 1);
        tableLayoutPanel1.Controls.Add(txtWord, 1, 1);
        tableLayoutPanel1.Controls.Add(label9, 0, 2);
        tableLayoutPanel1.Controls.Add(txtPhonetic, 1, 2);
        tableLayoutPanel1.Controls.Add(label10, 0, 3);
        tableLayoutPanel1.Controls.Add(txtPartOfSpeech, 1, 3);
        tableLayoutPanel1.Controls.Add(label11, 0, 4);
        tableLayoutPanel1.Controls.Add(txtMeaning, 1, 4);
        tableLayoutPanel1.Controls.Add(label12, 0, 5);
        tableLayoutPanel1.Controls.Add(txtExample, 1, 5);
        tableLayoutPanel1.Controls.Add(btnAddOrUpdateWord, 1, 6);
        tableLayoutPanel1.Dock = DockStyle.Fill;
        tableLayoutPanel1.Location = new Point(0, 0);
        tableLayoutPanel1.Margin = new Padding(6);
        tableLayoutPanel1.Name = "tableLayoutPanel1";
        tableLayoutPanel1.RowCount = 7;
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 144F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 144F));
        tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
        tableLayoutPanel1.Size = new Size(1222, 660);
        tableLayoutPanel1.TabIndex = 0;
        // 
        // label13
        // 
        label13.AutoSize = true;
        label13.Dock = DockStyle.Fill;
        label13.Location = new Point(6, 6);
        label13.Margin = new Padding(6);
        label13.Name = "label13";
        label13.Size = new Size(135, 48);
        label13.TabIndex = 1;
        label13.Text = "单词本：";
        label13.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // panelBookControls
        // 
        panelBookControls.ColumnCount = 2;
        panelBookControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        panelBookControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panelBookControls.Controls.Add(btnRefreshBooks, 0, 0);
        panelBookControls.Controls.Add(cmbBookId, 1, 0);
        panelBookControls.Dock = DockStyle.Fill;
        panelBookControls.Location = new Point(153, 6);
        panelBookControls.Margin = new Padding(6);
        panelBookControls.Name = "panelBookControls";
        panelBookControls.RowCount = 1;
        panelBookControls.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        panelBookControls.Size = new Size(1063, 48);
        panelBookControls.TabIndex = 11;
        // 
        // btnRefreshBooks
        // 
        btnRefreshBooks.Dock = DockStyle.Fill;
        btnRefreshBooks.Location = new Point(6, 6);
        btnRefreshBooks.Margin = new Padding(6);
        btnRefreshBooks.Name = "btnRefreshBooks";
        btnRefreshBooks.Size = new Size(80, 36);
        btnRefreshBooks.TabIndex = 10;
        btnRefreshBooks.Text = "刷新";
        btnRefreshBooks.UseVisualStyleBackColor = true;
        btnRefreshBooks.Click += btnRefreshBooks_Click;
        // 
        // cmbBookId
        // 
        cmbBookId.Dock = DockStyle.Fill;
        cmbBookId.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbBookId.FormattingEnabled = true;
        cmbBookId.Location = new Point(98, 6);
        cmbBookId.Margin = new Padding(6);
        cmbBookId.Name = "cmbBookId";
        cmbBookId.Size = new Size(959, 32);
        cmbBookId.TabIndex = 5;
        // 
        // label8
        // 
        label8.AutoSize = true;
        label8.Dock = DockStyle.Fill;
        label8.Location = new Point(6, 66);
        label8.Margin = new Padding(6);
        label8.Name = "label8";
        label8.Size = new Size(135, 48);
        label8.TabIndex = 0;
        label8.Text = "单词：";
        label8.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // txtWord
        // 
        txtWord.Dock = DockStyle.Fill;
        txtWord.Location = new Point(153, 66);
        txtWord.Margin = new Padding(6);
        txtWord.Name = "txtWord";
        txtWord.Size = new Size(1063, 30);
        txtWord.TabIndex = 0;
        // 
        // label9
        // 
        label9.AutoSize = true;
        label9.Dock = DockStyle.Fill;
        label9.Location = new Point(6, 126);
        label9.Margin = new Padding(6);
        label9.Name = "label9";
        label9.Size = new Size(135, 48);
        label9.TabIndex = 6;
        label9.Text = "音标：";
        label9.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // txtPhonetic
        // 
        txtPhonetic.Dock = DockStyle.Fill;
        txtPhonetic.Location = new Point(153, 126);
        txtPhonetic.Margin = new Padding(6);
        txtPhonetic.Name = "txtPhonetic";
        txtPhonetic.Size = new Size(1063, 30);
        txtPhonetic.TabIndex = 1;
        // 
        // label10
        // 
        label10.AutoSize = true;
        label10.Dock = DockStyle.Fill;
        label10.Location = new Point(6, 186);
        label10.Margin = new Padding(6);
        label10.Name = "label10";
        label10.Size = new Size(135, 48);
        label10.TabIndex = 7;
        label10.Text = "词性：";
        label10.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // txtPartOfSpeech
        // 
        txtPartOfSpeech.Dock = DockStyle.Fill;
        txtPartOfSpeech.Location = new Point(153, 186);
        txtPartOfSpeech.Margin = new Padding(6);
        txtPartOfSpeech.Name = "txtPartOfSpeech";
        txtPartOfSpeech.Size = new Size(1063, 30);
        txtPartOfSpeech.TabIndex = 2;
        // 
        // label11
        // 
        label11.AutoSize = true;
        label11.Dock = DockStyle.Fill;
        label11.Location = new Point(6, 246);
        label11.Margin = new Padding(6);
        label11.Name = "label11";
        label11.Size = new Size(135, 132);
        label11.TabIndex = 8;
        label11.Text = "词义：";
        // 
        // txtMeaning
        // 
        txtMeaning.Dock = DockStyle.Fill;
        txtMeaning.Location = new Point(153, 246);
        txtMeaning.Margin = new Padding(6);
        txtMeaning.Multiline = true;
        txtMeaning.Name = "txtMeaning";
        txtMeaning.Size = new Size(1063, 132);
        txtMeaning.TabIndex = 3;
        // 
        // label12
        // 
        label12.AutoSize = true;
        label12.Dock = DockStyle.Fill;
        label12.Location = new Point(6, 390);
        label12.Margin = new Padding(6);
        label12.Name = "label12";
        label12.Size = new Size(135, 132);
        label12.TabIndex = 9;
        label12.Text = "例句：";
        // 
        // txtExample
        // 
        txtExample.Dock = DockStyle.Fill;
        txtExample.Location = new Point(153, 390);
        txtExample.Margin = new Padding(6);
        txtExample.Multiline = true;
        txtExample.Name = "txtExample";
        txtExample.Size = new Size(1063, 132);
        txtExample.TabIndex = 4;
        // 
        // btnAddOrUpdateWord
        // 
        btnAddOrUpdateWord.Anchor = AnchorStyles.Left;
        btnAddOrUpdateWord.Location = new Point(153, 573);
        btnAddOrUpdateWord.Margin = new Padding(6);
        btnAddOrUpdateWord.Name = "btnAddOrUpdateWord";
        btnAddOrUpdateWord.Size = new Size(183, 42);
        btnAddOrUpdateWord.TabIndex = 6;
        btnAddOrUpdateWord.Text = "添加/修改单词";
        btnAddOrUpdateWord.UseVisualStyleBackColor = true;
        btnAddOrUpdateWord.Click += btnAddOrUpdateWord_Click;
        // 
        // CtlWordManager
        // 
        AutoScaleDimensions = new SizeF(11F, 24F);
        AutoScaleMode = AutoScaleMode.Font;
        Controls.Add(tableLayoutPanel1);
        Margin = new Padding(4);
        Name = "CtlWordManager";
        Size = new Size(1222, 660);
        tableLayoutPanel1.ResumeLayout(false);
        tableLayoutPanel1.PerformLayout();
        panelBookControls.ResumeLayout(false);
        ResumeLayout(false);

    }

    #endregion

    private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
        private System.Windows.Forms.TextBox txtWord;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.TextBox txtPhonetic;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.TextBox txtPartOfSpeech;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.TextBox txtMeaning;
        private System.Windows.Forms.Label label11;
        private System.Windows.Forms.TextBox txtExample;
        private System.Windows.Forms.Label label12;
        private System.Windows.Forms.ComboBox cmbBookId;
        private System.Windows.Forms.Label label13;
        private System.Windows.Forms.Button btnAddOrUpdateWord;
        private System.Windows.Forms.Button btnRefreshBooks;
        private System.Windows.Forms.TableLayoutPanel panelBookControls;
}