namespace Ruoyu.Study.Vocabulary.Test.Forms
{
    partial class FrmBookAttribute
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

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            mainLayout = new TableLayoutPanel();
            bookInfoGroup = new GroupBox();
            bookInfoLayout = new TableLayoutPanel();
            lblBookName = new Label();
            txtBookName = new TextBox();
            lblDescription = new Label();
            txtDescription = new TextBox();
            lblCategory = new Label();
            cmbCategory = new ComboBox();
            lblEducationLevel = new Label();
            cmbEducationLevel = new ComboBox();
            lblGrade = new Label();
            cmbGrade = new ComboBox();
            lblStatus = new Label();
            cmbStatus = new ComboBox();
            buttonPanel = new Panel();
            btnSave = new Button();
            btnCancel = new Button();
            mainLayout.SuspendLayout();
            bookInfoGroup.SuspendLayout();
            bookInfoLayout.SuspendLayout();
            buttonPanel.SuspendLayout();
            SuspendLayout();
            // 
            // mainLayout
            // 
            mainLayout.ColumnCount = 1;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            mainLayout.Controls.Add(bookInfoGroup, 0, 0);
            mainLayout.Controls.Add(buttonPanel, 0, 1);
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.Location = new Point(0, 0);
            mainLayout.Name = "mainLayout";
            mainLayout.Padding = new Padding(20);
            mainLayout.RowCount = 2;
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            mainLayout.Size = new Size(580, 420);
            mainLayout.TabIndex = 0;
            // 
            // bookInfoGroup
            // 
            bookInfoGroup.Controls.Add(bookInfoLayout);
            bookInfoGroup.Dock = DockStyle.Fill;
            bookInfoGroup.Font = new Font("Microsoft Sans Serif", 10F, FontStyle.Bold);
            bookInfoGroup.Location = new Point(23, 23);
            bookInfoGroup.Name = "bookInfoGroup";
            bookInfoGroup.Size = new Size(534, 304);
            bookInfoGroup.TabIndex = 0;
            bookInfoGroup.TabStop = false;
            bookInfoGroup.Text = "单词本信息";
            // 
            // bookInfoLayout
            // 
            bookInfoLayout.BackColor = Color.White;
            bookInfoLayout.ColumnCount = 2;
            bookInfoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            bookInfoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80F));
            bookInfoLayout.Controls.Add(lblBookName, 0, 0);
            bookInfoLayout.Controls.Add(txtBookName, 1, 0);
            bookInfoLayout.Controls.Add(lblDescription, 0, 1);
            bookInfoLayout.Controls.Add(txtDescription, 1, 1);
            bookInfoLayout.Controls.Add(lblCategory, 0, 2);
            bookInfoLayout.Controls.Add(cmbCategory, 1, 2);
            bookInfoLayout.Controls.Add(lblEducationLevel, 0, 3);
            bookInfoLayout.Controls.Add(cmbEducationLevel, 1, 3);
            bookInfoLayout.Controls.Add(lblGrade, 0, 4);
            bookInfoLayout.Controls.Add(cmbGrade, 1, 4);
            bookInfoLayout.Controls.Add(lblStatus, 0, 5);
            bookInfoLayout.Controls.Add(cmbStatus, 1, 5);
            bookInfoLayout.Dock = DockStyle.Fill;
            bookInfoLayout.Location = new Point(3, 19);
            bookInfoLayout.Name = "bookInfoLayout";
            bookInfoLayout.Padding = new Padding(20);
            bookInfoLayout.RowCount = 6;
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            bookInfoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            bookInfoLayout.Size = new Size(528, 282);
            bookInfoLayout.TabIndex = 0;
            // 
            // lblBookName
            // 
            lblBookName.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblBookName.AutoSize = true;
            lblBookName.Font = new Font("Microsoft Sans Serif", 10F);
            lblBookName.Location = new Point(43, 25);
            lblBookName.Margin = new Padding(0, 5, 10, 5);
            lblBookName.Name = "lblBookName";
            lblBookName.Size = new Size(64, 34);
            lblBookName.TabIndex = 0;
            lblBookName.Text = "单词本名称：";
            lblBookName.TextAlign = ContentAlignment.MiddleRight;
            // 
            // txtBookName
            // 
            txtBookName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            txtBookName.Font = new Font("Microsoft Sans Serif", 10F);
            txtBookName.Location = new Point(122, 33);
            txtBookName.Margin = new Padding(5, 5, 0, 5);
            txtBookName.Name = "txtBookName";
            txtBookName.Size = new Size(386, 23);
            txtBookName.TabIndex = 1;
            // 
            // lblDescription
            // 
            lblDescription.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblDescription.AutoSize = true;
            lblDescription.Font = new Font("Microsoft Sans Serif", 10F);
            lblDescription.Location = new Point(57, 75);
            lblDescription.Margin = new Padding(0, 5, 10, 0);
            lblDescription.Name = "lblDescription";
            lblDescription.Size = new Size(50, 1);
            lblDescription.TabIndex = 2;
            lblDescription.Text = "描述：";
            lblDescription.TextAlign = ContentAlignment.MiddleRight;
            // 
            // txtDescription
            // 
            txtDescription.Dock = DockStyle.Fill;
            txtDescription.Font = new Font("Microsoft Sans Serif", 10F);
            txtDescription.Location = new Point(122, 75);
            txtDescription.Margin = new Padding(5, 5, 0, 5);
            txtDescription.Multiline = true;
            txtDescription.Name = "txtDescription";
            txtDescription.ScrollBars = ScrollBars.Vertical;
            txtDescription.Size = new Size(386, 1);
            txtDescription.TabIndex = 3;
            // 
            // lblCategory
            // 
            lblCategory.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblCategory.AutoSize = true;
            lblCategory.Font = new Font("Microsoft Sans Serif", 10F);
            lblCategory.Location = new Point(57, 67);
            lblCategory.Margin = new Padding(0, 5, 10, 5);
            lblCategory.Name = "lblCategory";
            lblCategory.Size = new Size(50, 17);
            lblCategory.TabIndex = 4;
            lblCategory.Text = "分类：";
            lblCategory.TextAlign = ContentAlignment.MiddleRight;
            // 
            // cmbCategory
            // 
            cmbCategory.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbCategory.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCategory.Font = new Font("Microsoft Sans Serif", 10F);
            cmbCategory.FormattingEnabled = true;
            cmbCategory.Location = new Point(122, 74);
            cmbCategory.Margin = new Padding(5, 5, 0, 5);
            cmbCategory.Name = "cmbCategory";
            cmbCategory.Size = new Size(386, 24);
            cmbCategory.TabIndex = 5;
            // 
            // lblEducationLevel
            // 
            lblEducationLevel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblEducationLevel.AutoSize = true;
            lblEducationLevel.Font = new Font("Microsoft Sans Serif", 10F);
            lblEducationLevel.Location = new Point(29, 117);
            lblEducationLevel.Margin = new Padding(0, 5, 10, 5);
            lblEducationLevel.Name = "lblEducationLevel";
            lblEducationLevel.Size = new Size(78, 17);
            lblEducationLevel.TabIndex = 6;
            lblEducationLevel.Text = "教育阶段：";
            lblEducationLevel.TextAlign = ContentAlignment.MiddleRight;
            // 
            // cmbEducationLevel
            // 
            cmbEducationLevel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbEducationLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbEducationLevel.Font = new Font("Microsoft Sans Serif", 10F);
            cmbEducationLevel.FormattingEnabled = true;
            cmbEducationLevel.Location = new Point(122, 124);
            cmbEducationLevel.Margin = new Padding(5, 5, 0, 5);
            cmbEducationLevel.Name = "cmbEducationLevel";
            cmbEducationLevel.Size = new Size(386, 24);
            cmbEducationLevel.TabIndex = 7;
            cmbEducationLevel.SelectedIndexChanged += CmbEducationLevel_SelectedIndexChanged;
            // 
            // lblGrade
            // 
            lblGrade.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblGrade.AutoSize = true;
            lblGrade.Font = new Font("Microsoft Sans Serif", 10F);
            lblGrade.Location = new Point(57, 167);
            lblGrade.Margin = new Padding(0, 5, 10, 5);
            lblGrade.Name = "lblGrade";
            lblGrade.Size = new Size(50, 17);
            lblGrade.TabIndex = 8;
            lblGrade.Text = "年级：";
            lblGrade.TextAlign = ContentAlignment.MiddleRight;
            // 
            // cmbGrade
            // 
            cmbGrade.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbGrade.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbGrade.Font = new Font("Microsoft Sans Serif", 10F);
            cmbGrade.FormattingEnabled = true;
            cmbGrade.Location = new Point(122, 174);
            cmbGrade.Margin = new Padding(5, 5, 0, 5);
            cmbGrade.Name = "cmbGrade";
            cmbGrade.Size = new Size(386, 24);
            cmbGrade.TabIndex = 9;
            // 
            // lblStatus
            // 
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft Sans Serif", 10F);
            lblStatus.Location = new Point(57, 217);
            lblStatus.Margin = new Padding(0, 5, 10, 5);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(50, 17);
            lblStatus.TabIndex = 10;
            lblStatus.Text = "状态：";
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            // 
            // cmbStatus
            // 
            cmbStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            cmbStatus.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStatus.Font = new Font("Microsoft Sans Serif", 10F);
            cmbStatus.FormattingEnabled = true;
            cmbStatus.Location = new Point(122, 224);
            cmbStatus.Margin = new Padding(5, 5, 0, 5);
            cmbStatus.Name = "cmbStatus";
            cmbStatus.Size = new Size(386, 24);
            cmbStatus.TabIndex = 11;
            // 
            // buttonPanel
            // 
            buttonPanel.Controls.Add(btnSave);
            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.Location = new Point(23, 333);
            buttonPanel.Name = "buttonPanel";
            buttonPanel.Padding = new Padding(0, 10, 20, 0);
            buttonPanel.Size = new Size(534, 64);
            buttonPanel.TabIndex = 1;
            // 
            // btnSave
            // 
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.BackColor = Color.FromArgb(40, 167, 69);
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.Font = new Font("Microsoft Sans Serif", 10F, FontStyle.Bold);
            btnSave.ForeColor = Color.White;
            btnSave.Location = new Point(324, 10);
            btnSave.Name = "btnSave";
            btnSave.Size = new Size(100, 40);
            btnSave.TabIndex = 0;
            btnSave.Text = "保存";
            btnSave.UseVisualStyleBackColor = false;
            btnSave.Click += BtnSave_Click;
            // 
            // btnCancel
            // 
            btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCancel.BackColor = Color.FromArgb(108, 117, 125);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.Font = new Font("Microsoft Sans Serif", 10F, FontStyle.Bold);
            btnCancel.ForeColor = Color.White;
            btnCancel.Location = new Point(430, 10);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(100, 40);
            btnCancel.TabIndex = 1;
            btnCancel.Text = "取消";
            btnCancel.UseVisualStyleBackColor = false;
            btnCancel.Click += BtnCancel_Click;
            // 
            // FrmBookAttribute
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(580, 420);
            Controls.Add(mainLayout);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "FrmBookAttribute";
            StartPosition = FormStartPosition.CenterParent;
            Text = "新增单词本";
            mainLayout.ResumeLayout(false);
            bookInfoGroup.ResumeLayout(false);
            bookInfoLayout.ResumeLayout(false);
            bookInfoLayout.PerformLayout();
            buttonPanel.ResumeLayout(false);
            ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TableLayoutPanel mainLayout;
        private System.Windows.Forms.GroupBox bookInfoGroup;
        private System.Windows.Forms.TableLayoutPanel bookInfoLayout;
        private System.Windows.Forms.Label lblBookName;
        private System.Windows.Forms.TextBox txtBookName;
        private System.Windows.Forms.Label lblDescription;
        private System.Windows.Forms.TextBox txtDescription;
        private System.Windows.Forms.Label lblCategory;
        private System.Windows.Forms.ComboBox cmbCategory;
        private System.Windows.Forms.Label lblEducationLevel;
        private System.Windows.Forms.ComboBox cmbEducationLevel;
        private System.Windows.Forms.Label lblGrade;
        private System.Windows.Forms.ComboBox cmbGrade;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.ComboBox cmbStatus;
        private System.Windows.Forms.Panel buttonPanel;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Button btnCancel;
    }
}