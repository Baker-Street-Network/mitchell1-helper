namespace Mitchell1Helper;

partial class SettingsForm
{
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.Label sqlSectionLabel;
    private System.Windows.Forms.Label sqlConnectionLabel;
    private System.Windows.Forms.TextBox sqlConnectionStringTextBox;
    private System.Windows.Forms.Label odooSectionLabel;
    private System.Windows.Forms.Label odooUrlLabel;
    private System.Windows.Forms.TextBox odooUrlTextBox;
    private System.Windows.Forms.Label odooDatabaseLabel;
    private System.Windows.Forms.TextBox odooDatabaseTextBox;
    private System.Windows.Forms.Label odooUsernameLabel;
    private System.Windows.Forms.TextBox odooUsernameTextBox;
    private System.Windows.Forms.Label odooPasswordLabel;
    private System.Windows.Forms.TextBox odooPasswordTextBox;
    private System.Windows.Forms.Label batchSizeLabel;
    private System.Windows.Forms.NumericUpDown batchSizeNumericUpDown;
    private System.Windows.Forms.Button saveButton;
    private System.Windows.Forms.Button reloadButton;
    private System.Windows.Forms.Label statusLabel;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        sqlSectionLabel = new Label();
        sqlConnectionLabel = new Label();
        sqlConnectionStringTextBox = new TextBox();
        odooSectionLabel = new Label();
        odooUrlLabel = new Label();
        odooUrlTextBox = new TextBox();
        odooDatabaseLabel = new Label();
        odooDatabaseTextBox = new TextBox();
        odooUsernameLabel = new Label();
        odooUsernameTextBox = new TextBox();
        odooPasswordLabel = new Label();
        odooPasswordTextBox = new TextBox();
        saveButton = new Button();
        reloadButton = new Button();
        statusLabel = new Label();
        textBox1 = new TextBox();
        batchSizeLabel = new Label();
        batchSizeNumericUpDown = new NumericUpDown();
        SuspendLayout();
        // 
        // sqlSectionLabel
        // 
        sqlSectionLabel.AutoSize = true;
        sqlSectionLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
        sqlSectionLabel.Location = new Point(16, 16);
        sqlSectionLabel.Name = "sqlSectionLabel";
        sqlSectionLabel.Size = new Size(87, 20);
        sqlSectionLabel.TabIndex = 0;
        sqlSectionLabel.Text = "SQL Source";
        // 
        // sqlConnectionLabel
        // 
        sqlConnectionLabel.AutoSize = true;
        sqlConnectionLabel.Location = new Point(24, 50);
        sqlConnectionLabel.Name = "sqlConnectionLabel";
        sqlConnectionLabel.Size = new Size(103, 15);
        sqlConnectionLabel.TabIndex = 1;
        sqlConnectionLabel.Text = "Connection String";
        // 
        // sqlConnectionStringTextBox
        // 
        sqlConnectionStringTextBox.Location = new Point(133, 47);
        sqlConnectionStringTextBox.Name = "sqlConnectionStringTextBox";
        sqlConnectionStringTextBox.Size = new Size(501, 23);
        sqlConnectionStringTextBox.TabIndex = 2;
        // 
        // odooSectionLabel
        // 
        odooSectionLabel.AutoSize = true;
        odooSectionLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
        odooSectionLabel.Location = new Point(16, 81);
        odooSectionLabel.Name = "odooSectionLabel";
        odooSectionLabel.Size = new Size(101, 20);
        odooSectionLabel.TabIndex = 3;
        odooSectionLabel.Text = "Odoo 19 RPC";
        // 
        // odooUrlLabel
        // 
        odooUrlLabel.AutoSize = true;
        odooUrlLabel.Location = new Point(24, 108);
        odooUrlLabel.Name = "odooUrlLabel";
        odooUrlLabel.Size = new Size(61, 15);
        odooUrlLabel.TabIndex = 4;
        odooUrlLabel.Text = "Odoo URL";
        // 
        // odooUrlTextBox
        // 
        odooUrlTextBox.Location = new Point(96, 108);
        odooUrlTextBox.Name = "odooUrlTextBox";
        odooUrlTextBox.Size = new Size(220, 23);
        odooUrlTextBox.TabIndex = 5;
        // 
        // odooDatabaseLabel
        // 
        odooDatabaseLabel.AutoSize = true;
        odooDatabaseLabel.Location = new Point(24, 133);
        odooDatabaseLabel.Name = "odooDatabaseLabel";
        odooDatabaseLabel.Size = new Size(55, 15);
        odooDatabaseLabel.TabIndex = 6;
        odooDatabaseLabel.Text = "Database";
        // 
        // odooDatabaseTextBox
        // 
        odooDatabaseTextBox.Location = new Point(96, 131);
        odooDatabaseTextBox.Name = "odooDatabaseTextBox";
        odooDatabaseTextBox.Size = new Size(220, 23);
        odooDatabaseTextBox.TabIndex = 7;
        // 
        // odooUsernameLabel
        // 
        odooUsernameLabel.AutoSize = true;
        odooUsernameLabel.Location = new Point(24, 157);
        odooUsernameLabel.Name = "odooUsernameLabel";
        odooUsernameLabel.Size = new Size(60, 15);
        odooUsernameLabel.TabIndex = 8;
        odooUsernameLabel.Text = "Username";
        // 
        // odooUsernameTextBox
        // 
        odooUsernameTextBox.Location = new Point(96, 155);
        odooUsernameTextBox.Name = "odooUsernameTextBox";
        odooUsernameTextBox.Size = new Size(220, 23);
        odooUsernameTextBox.TabIndex = 9;
        // 
        // odooPasswordLabel
        // 
        odooPasswordLabel.AutoSize = true;
        odooPasswordLabel.Location = new Point(24, 179);
        odooPasswordLabel.Name = "odooPasswordLabel";
        odooPasswordLabel.Size = new Size(57, 15);
        odooPasswordLabel.TabIndex = 10;
        odooPasswordLabel.Text = "Password";
        // 
        // odooPasswordTextBox
        // 
        odooPasswordTextBox.Location = new Point(96, 180);
        odooPasswordTextBox.Name = "odooPasswordTextBox";
        odooPasswordTextBox.Size = new Size(220, 23);
        odooPasswordTextBox.TabIndex = 11;
        odooPasswordTextBox.UseSystemPasswordChar = true;
        // 
        // batchSizeLabel
        // 
        batchSizeLabel.AutoSize = true;
        batchSizeLabel.Location = new Point(24, 204);
        batchSizeLabel.Name = "batchSizeLabel";
        batchSizeLabel.Size = new Size(60, 15);
        batchSizeLabel.TabIndex = 16;
        batchSizeLabel.Text = "Batch Size";
        // 
        // batchSizeNumericUpDown
        // 
        batchSizeNumericUpDown.Location = new Point(96, 202);
        batchSizeNumericUpDown.Minimum = 1;
        batchSizeNumericUpDown.Maximum = 10000;
        batchSizeNumericUpDown.Name = "batchSizeNumericUpDown";
        batchSizeNumericUpDown.Size = new Size(80, 23);
        batchSizeNumericUpDown.TabIndex = 17;
        // 
        // saveButton
        // 
        saveButton.Location = new Point(20, 246);
        saveButton.Name = "saveButton";
        saveButton.Size = new Size(130, 34);
        saveButton.TabIndex = 12;
        saveButton.Text = "Save Settings";
        saveButton.UseVisualStyleBackColor = true;
        saveButton.Click += saveButton_Click;
        // 
        // reloadButton
        // 
        reloadButton.Location = new Point(156, 246);
        reloadButton.Name = "reloadButton";
        reloadButton.Size = new Size(98, 34);
        reloadButton.TabIndex = 13;
        reloadButton.Text = "Reload";
        reloadButton.UseVisualStyleBackColor = true;
        reloadButton.Click += reloadButton_Click;
        // 
        // statusLabel
        // 
        statusLabel.AutoSize = true;
        statusLabel.ForeColor = Color.DarkGreen;
        statusLabel.Location = new Point(344, 91);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(32, 15);
        statusLabel.TabIndex = 14;
        statusLabel.Text = "Logs";
        // 
        // textBox1
        // 
        textBox1.Location = new Point(344, 108);
        textBox1.Margin = new Padding(2, 2, 2, 2);
        textBox1.Multiline = true;
        textBox1.Name = "textBox1";
        textBox1.Size = new Size(290, 162);
        textBox1.TabIndex = 15;
        // 
        // SettingsForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoScroll = true;
        ClientSize = new Size(647, 306);
        Controls.Add(textBox1);
        Controls.Add(statusLabel);
        Controls.Add(reloadButton);
        Controls.Add(saveButton);
        Controls.Add(batchSizeNumericUpDown);
        Controls.Add(batchSizeLabel);
        Controls.Add(odooPasswordTextBox);
        Controls.Add(odooPasswordLabel);
        Controls.Add(odooUsernameTextBox);
        Controls.Add(odooUsernameLabel);
        Controls.Add(odooDatabaseTextBox);
        Controls.Add(odooDatabaseLabel);
        Controls.Add(odooUrlTextBox);
        Controls.Add(odooUrlLabel);
        Controls.Add(odooSectionLabel);
        Controls.Add(sqlConnectionStringTextBox);
        Controls.Add(sqlConnectionLabel);
        Controls.Add(sqlSectionLabel);
        Name = "SettingsForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Mitchell1 Helper Settings";
        ResumeLayout(false);
        PerformLayout();
    }

    private TextBox textBox1;
}
