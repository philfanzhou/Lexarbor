namespace Ruoyu.Study.Vocabulary.Test.Controls
{
    partial class CtlBookManager
    {
        /// <summary> 
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel;
        private System.Windows.Forms.Panel headerPanel;
        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Button btnDeleteBook;
        private System.Windows.Forms.DataGridView dgvBooks;
        private System.Windows.Forms.Panel paginationPanel;
        private System.Windows.Forms.Label lblPageInfo;
        private System.Windows.Forms.Button btnFirst;
        private System.Windows.Forms.Button btnPrevious;
        private System.Windows.Forms.Button btnNext;
        private System.Windows.Forms.Button btnLast;
        private System.Windows.Forms.Button btnAddBook;
        private System.Windows.Forms.ComboBox cmbPageSize;
        private System.Windows.Forms.Label lblPageSize;

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

        #region 组件设计器生成的代码

        /// <summary> 
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            tableLayoutPanel = new TableLayoutPanel();
            headerPanel = new Panel();
            lblTitle = new Label();
            btnRefresh = new Button();
            btnDeleteBook = new Button();
            btnAddBook = new Button();
            dgvBooks = new DataGridView();
            paginationPanel = new Panel();
            cmbPageSize = new ComboBox();
            lblPageSize = new Label();
            lblPageInfo = new Label();
            btnFirst = new Button();
            btnPrevious = new Button();
            btnNext = new Button();
            btnLast = new Button();
            tableLayoutPanel.SuspendLayout();
            headerPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dgvBooks).BeginInit();
            paginationPanel.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel
            // 
            tableLayoutPanel.ColumnCount = 1;
            tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel.Controls.Add(headerPanel, 0, 0);
            tableLayoutPanel.Controls.Add(dgvBooks, 0, 1);
            tableLayoutPanel.Controls.Add(paginationPanel, 0, 2);
            tableLayoutPanel.Dock = DockStyle.Fill;
            tableLayoutPanel.Location = new Point(0, 0);
            tableLayoutPanel.Margin = new Padding(4);
            tableLayoutPanel.Name = "tableLayoutPanel";
            tableLayoutPanel.RowCount = 3;
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            tableLayoutPanel.Size = new Size(933, 498);
            tableLayoutPanel.TabIndex = 0;
            // 
            // headerPanel
            // 
            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(btnRefresh);
            headerPanel.Controls.Add(btnDeleteBook);
            headerPanel.Controls.Add(btnAddBook);
            headerPanel.Dock = DockStyle.Fill;
            headerPanel.Location = new Point(4, 4);
            headerPanel.Margin = new Padding(4);
            headerPanel.Name = "headerPanel";
            headerPanel.Padding = new Padding(10);
            headerPanel.Size = new Size(925, 52);
            headerPanel.TabIndex = 0;
            // 
            // lblTitle
            // 
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            lblTitle.Location = new Point(10, 10);
            lblTitle.Margin = new Padding(4, 0, 4, 0);
            lblTitle.Name = "lblTitle";
            lblTitle.Size = new Size(94, 20);
            lblTitle.TabIndex = 0;
            lblTitle.Text = "单词本管理";
            // 
            // btnRefresh
            // 
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Location = new Point(651, 10);
            btnRefresh.Margin = new Padding(4);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(80, 30);
            btnRefresh.TabIndex = 5;
            btnRefresh.Text = "刷新";
            btnRefresh.UseVisualStyleBackColor = true;
            btnRefresh.Click += BtnRefresh_Click;
            // 
            // btnDeleteBook
            // 
            btnDeleteBook.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDeleteBook.Location = new Point(741, 10);
            btnDeleteBook.Margin = new Padding(4);
            btnDeleteBook.Name = "btnDeleteBook";
            btnDeleteBook.Size = new Size(80, 30);
            btnDeleteBook.TabIndex = 6;
            btnDeleteBook.Text = "删除";
            btnDeleteBook.UseVisualStyleBackColor = true;
            btnDeleteBook.Click += BtnDeleteBook_Click;
            // 
            // btnAddBook
            // 
            btnAddBook.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnAddBook.Location = new Point(831, 10);
            btnAddBook.Margin = new Padding(4);
            btnAddBook.Name = "btnAddBook";
            btnAddBook.Size = new Size(80, 30);
            btnAddBook.TabIndex = 7;
            btnAddBook.Text = "新增";
            btnAddBook.UseVisualStyleBackColor = true;
            btnAddBook.Click += BtnAddBook_Click;
            // 
            // dgvBooks
            // 
            dgvBooks.AllowUserToAddRows = false;
            dgvBooks.AllowUserToDeleteRows = false;
            dgvBooks.AllowUserToOrderColumns = true;
            dgvBooks.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgvBooks.Dock = DockStyle.Fill;
            dgvBooks.Location = new Point(4, 64);
            dgvBooks.Margin = new Padding(4);
            dgvBooks.Name = "dgvBooks";
            dgvBooks.ReadOnly = true;
            dgvBooks.RowHeadersVisible = false;
            dgvBooks.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvBooks.Size = new Size(925, 370);
            dgvBooks.TabIndex = 1;
            // 
            // paginationPanel
            // 
            paginationPanel.Controls.Add(cmbPageSize);
            paginationPanel.Controls.Add(lblPageSize);
            paginationPanel.Controls.Add(lblPageInfo);
            paginationPanel.Controls.Add(btnFirst);
            paginationPanel.Controls.Add(btnPrevious);
            paginationPanel.Controls.Add(btnNext);
            paginationPanel.Controls.Add(btnLast);
            paginationPanel.Dock = DockStyle.Fill;
            paginationPanel.Location = new Point(4, 442);
            paginationPanel.Margin = new Padding(4);
            paginationPanel.Name = "paginationPanel";
            paginationPanel.Padding = new Padding(10);
            paginationPanel.Size = new Size(925, 52);
            paginationPanel.TabIndex = 2;
            // 
            // cmbPageSize
            // 
            cmbPageSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPageSize.FormattingEnabled = true;
            cmbPageSize.Items.AddRange(new object[] { "10", "20", "50", "100" });
            cmbPageSize.Location = new Point(300, 12);
            cmbPageSize.Name = "cmbPageSize";
            cmbPageSize.Size = new Size(60, 25);
            cmbPageSize.TabIndex = 11;
            cmbPageSize.SelectedIndexChanged += cmbPageSize_SelectedIndexChanged;
            // 
            // lblPageSize
            // 
            lblPageSize.AutoSize = true;
            lblPageSize.Location = new Point(230, 15);
            lblPageSize.Margin = new Padding(4, 0, 4, 0);
            lblPageSize.Name = "lblPageSize";
            lblPageSize.Size = new Size(59, 17);
            lblPageSize.TabIndex = 10;
            lblPageSize.Text = "每页数量:";
            // 
            // lblPageInfo
            // 
            lblPageInfo.AutoSize = true;
            lblPageInfo.Location = new Point(10, 15);
            lblPageInfo.Margin = new Padding(4, 0, 4, 0);
            lblPageInfo.Name = "lblPageInfo";
            lblPageInfo.Size = new Size(173, 17);
            lblPageInfo.TabIndex = 0;
            lblPageInfo.Text = "第 1 页，共 1 页，共 0 条记录";
            // 
            // btnFirst
            // 
            btnFirst.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFirst.Location = new Point(595, 10);
            btnFirst.Margin = new Padding(4);
            btnFirst.Name = "btnFirst";
            btnFirst.Size = new Size(60, 30);
            btnFirst.TabIndex = 6;
            btnFirst.Text = "首页";
            btnFirst.UseVisualStyleBackColor = true;
            btnFirst.Click += BtnFirst_Click;
            // 
            // btnPrevious
            // 
            btnPrevious.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnPrevious.Location = new Point(663, 10);
            btnPrevious.Margin = new Padding(4);
            btnPrevious.Name = "btnPrevious";
            btnPrevious.Size = new Size(60, 30);
            btnPrevious.TabIndex = 7;
            btnPrevious.Text = "上一页";
            btnPrevious.UseVisualStyleBackColor = true;
            btnPrevious.Click += BtnPrevious_Click;
            // 
            // btnNext
            // 
            btnNext.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnNext.Location = new Point(731, 10);
            btnNext.Margin = new Padding(4);
            btnNext.Name = "btnNext";
            btnNext.Size = new Size(60, 30);
            btnNext.TabIndex = 8;
            btnNext.Text = "下一页";
            btnNext.UseVisualStyleBackColor = true;
            btnNext.Click += BtnNext_Click;
            // 
            // btnLast
            // 
            btnLast.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnLast.Location = new Point(799, 10);
            btnLast.Margin = new Padding(4);
            btnLast.Name = "btnLast";
            btnLast.Size = new Size(60, 30);
            btnLast.TabIndex = 9;
            btnLast.Text = "末页";
            btnLast.UseVisualStyleBackColor = true;
            btnLast.Click += BtnLast_Click;
            // 
            // BookManager
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(tableLayoutPanel);
            Margin = new Padding(4);
            Name = "BookManager";
            Size = new Size(933, 498);
            tableLayoutPanel.ResumeLayout(false);
            headerPanel.ResumeLayout(false);
            headerPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)dgvBooks).EndInit();
            paginationPanel.ResumeLayout(false);
            paginationPanel.PerformLayout();
            ResumeLayout(false);
        }

        private void InitializeDataGridViewColumns()
        {
            dgvBooks.Columns.Clear();
            
            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Id",
                HeaderText = "ID",
                DataPropertyName = "Id",
                Width = 80
            });

            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "BookName",
                HeaderText = "名称",
                DataPropertyName = "BookName",
                Width = 200
            });

            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Description",
                HeaderText = "描述",
                DataPropertyName = "Description",
                Width = 300
            });

            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Category",
                HeaderText = "分类",
                DataPropertyName = "Category",
                Width = 120
            });

            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "EducationLevel",
                HeaderText = "教育阶段",
                DataPropertyName = "EducationLevel",
                Width = 100
            });

            dgvBooks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Status",
                HeaderText = "状态",
                DataPropertyName = "Status",
                Width = 80
            });
        }

        #endregion
    }
}