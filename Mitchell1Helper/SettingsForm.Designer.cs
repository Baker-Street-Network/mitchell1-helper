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
        SuspendLayout();
        // 
        // sqlSectionLabel
        // 
        sqlSectionLabel.AutoSize = true;
        sqlSectionLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
        sqlSectionLabel.Location = new Point(23, 27);
        sqlSectionLabel.Margin = new Padding(4, 0, 4, 0);
        sqlSectionLabel.Name = "sqlSectionLabel";
        sqlSectionLabel.Size = new Size(129, 30);
        sqlSectionLabel.TabIndex = 0;
        sqlSectionLabel.Text = "SQL Source";
        // 
        // sqlConnectionLabel
        // 
        sqlConnectionLabel.AutoSize = true;
        sqlConnectionLabel.Location = new Point(29, 83);
        sqlConnectionLabel.Margin = new Padding(4, 0, 4, 0);
        sqlConnectionLabel.Name = "sqlConnectionLabel";
        sqlConnectionLabel.Size = new Size(153, 25);
        sqlConnectionLabel.TabIndex = 1;
        sqlConnectionLabel.Text = "Connection String";
        // 
        // sqlConnectionStringTextBox
        // 
        sqlConnectionStringTextBox.Location = new Point(190, 83);
        sqlConnectionStringTextBox.Margin = new Padding(4, 5, 4, 5);
        sqlConnectionStringTextBox.Name = "sqlConnectionStringTextBox";
        sqlConnectionStringTextBox.Size = new Size(630, 31);
        sqlConnectionStringTextBox.TabIndex = 2;
        // 
        // odooSectionLabel
        // 
        odooSectionLabel.AutoSize = true;
        odooSectionLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
        odooSectionLabel.Location = new Point(23, 135);
        odooSectionLabel.Margin = new Padding(4, 0, 4, 0);
        odooSectionLabel.Name = "odooSectionLabel";
        odooSectionLabel.Size = new Size(150, 30);
        odooSectionLabel.TabIndex = 3;
        odooSectionLabel.Text = "Odoo 19 RPC";
        // 
        // odooUrlLabel
        // 
        odooUrlLabel.AutoSize = true;
        odooUrlLabel.Location = new Point(34, 180);
        odooUrlLabel.Margin = new Padding(4, 0, 4, 0);
        odooUrlLabel.Name = "odooUrlLabel";
        odooUrlLabel.Size = new Size(95, 25);
        odooUrlLabel.TabIndex = 4;
        odooUrlLabel.Text = "Odoo URL";
        // 
        // odooUrlTextBox
        // 
        odooUrlTextBox.Location = new Point(137, 180);
        odooUrlTextBox.Margin = new Padding(4, 5, 4, 5);
        odooUrlTextBox.Name = "odooUrlTextBox";
        odooUrlTextBox.Size = new Size(313, 31);
        odooUrlTextBox.TabIndex = 5;
        // 
        // odooDatabaseLabel
        // 
        odooDatabaseLabel.AutoSize = true;
        odooDatabaseLabel.Location = new Point(34, 221);
        odooDatabaseLabel.Margin = new Padding(4, 0, 4, 0);
        odooDatabaseLabel.Name = "odooDatabaseLabel";
        odooDatabaseLabel.Size = new Size(86, 25);
        odooDatabaseLabel.TabIndex = 6;
        odooDatabaseLabel.Text = "Database";
        // 
        // odooDatabaseTextBox
        // 
        odooDatabaseTextBox.Location = new Point(137, 218);
        odooDatabaseTextBox.Margin = new Padding(4, 5, 4, 5);
        odooDatabaseTextBox.Name = "odooDatabaseTextBox";
        odooDatabaseTextBox.Size = new Size(313, 31);
        odooDatabaseTextBox.TabIndex = 7;
        // 
        // odooUsernameLabel
        // 
        odooUsernameLabel.AutoSize = true;
        odooUsernameLabel.Location = new Point(34, 262);
        odooUsernameLabel.Margin = new Padding(4, 0, 4, 0);
        odooUsernameLabel.Name = "odooUsernameLabel";
        odooUsernameLabel.Size = new Size(91, 25);
        odooUsernameLabel.TabIndex = 8;
        odooUsernameLabel.Text = "Username";
        // 
        // odooUsernameTextBox
        // 
        odooUsernameTextBox.Location = new Point(137, 259);
        odooUsernameTextBox.Margin = new Padding(4, 5, 4, 5);
        odooUsernameTextBox.Name = "odooUsernameTextBox";
        odooUsernameTextBox.Size = new Size(313, 31);
        odooUsernameTextBox.TabIndex = 9;
        // 
        // odooPasswordLabel
        // 
        odooPasswordLabel.AutoSize = true;
        odooPasswordLabel.Location = new Point(34, 298);
        odooPasswordLabel.Margin = new Padding(4, 0, 4, 0);
        odooPasswordLabel.Name = "odooPasswordLabel";
        odooPasswordLabel.Size = new Size(87, 25);
        odooPasswordLabel.TabIndex = 10;
        odooPasswordLabel.Text = "Password";
        // 
        // odooPasswordTextBox
        // 
        odooPasswordTextBox.Location = new Point(137, 300);
        odooPasswordTextBox.Margin = new Padding(4, 5, 4, 5);
        odooPasswordTextBox.Name = "odooPasswordTextBox";
        odooPasswordTextBox.Size = new Size(313, 31);
        odooPasswordTextBox.TabIndex = 11;
        odooPasswordTextBox.UseSystemPasswordChar = true;
        // 
        // saveButton
        // 
        saveButton.Location = new Point(29, 368);
        saveButton.Margin = new Padding(4, 5, 4, 5);
        saveButton.Name = "saveButton";
        saveButton.Size = new Size(186, 57);
        saveButton.TabIndex = 12;
        saveButton.Text = "Save Settings";
        saveButton.UseVisualStyleBackColor = true;
        saveButton.Click += saveButton_Click;
        // 
        // reloadButton
        // 
        reloadButton.Location = new Point(223, 368);
        reloadButton.Margin = new Padding(4, 5, 4, 5);
        reloadButton.Name = "reloadButton";
        reloadButton.Size = new Size(140, 57);
        reloadButton.TabIndex = 13;
        reloadButton.Text = "Reload";
        reloadButton.UseVisualStyleBackColor = true;
        reloadButton.Click += reloadButton_Click;
        // 
        // statusLabel
        // 
        statusLabel.AutoSize = true;
        statusLabel.ForeColor = Color.DarkGreen;
        statusLabel.Location = new Point(491, 152);
        statusLabel.Margin = new Padding(4, 0, 4, 0);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(50, 25);
        statusLabel.TabIndex = 14;
        statusLabel.Text = "Logs";
        // 
        // textBox1
        // 
        textBox1.Location = new Point(491, 180);
        textBox1.Multiline = true;
        textBox1.Name = "textBox1";
        textBox1.Size = new Size(582, 268);
        textBox1.TabIndex = 15;
        // 
        // SettingsForm
        // 
        AutoScaleDimensions = new SizeF(10F, 25F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoScroll = true;
        ClientSize = new Size(1085, 480);
        Controls.Add(textBox1);
        Controls.Add(statusLabel);
        Controls.Add(reloadButton);
        Controls.Add(saveButton);
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
        Margin = new Padding(4, 5, 4, 5);
        Name = "SettingsForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Mitchell1 Helper Settings";
        ResumeLayout(false);
        PerformLayout();
    }

    private TextBox textBox1;
}
