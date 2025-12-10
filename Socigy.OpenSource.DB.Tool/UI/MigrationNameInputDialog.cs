
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if IsWindows
namespace Socigy.OpenSource.DB.Tool.UI
{
    public static class MigrationNameInputDialog
    {
        [SupportedOSPlatform("windows6.1")]
        public static string? Show(string title, string prompt)
        {
            // 1. Create the Form (The Window)
            using var form = new Form()
            {
                Text = title,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(400, 150),
                TopMost = true // Ensures it appears on top of the console
            };

            // 2. Create the Label (Prompt)
            var label = new System.Windows.Forms.Label()
            {
                Text = prompt,
                Left = 20,
                Top = 20,
                Width = 360,
                AutoSize = true
            };

            // 3. Create the Text Box (Input)
            var textBox = new TextBox()
            {
                Left = 20,
                Top = 50,
                Width = 360
            };

            // 4. Create the Buttons
            var buttonOk = new Button()
            {
                Text = "Ok",
                Left = 210, // Positioned to the right
                Top = 100,
                Width = 80,
                DialogResult = DialogResult.OK // Tells the form this button means "Success"
            };

            var buttonCancel = new Button()
            {
                Text = "Cancel",
                Left = 300,
                Top = 100,
                Width = 80,
                DialogResult = DialogResult.Cancel // Tells the form this button means "Abort"
            };

            // 5. Add controls to the form
            form.Controls.Add(label);
            form.Controls.Add(textBox);
            form.Controls.Add(buttonOk);
            form.Controls.Add(buttonCancel);

            // 6. Set Default Buttons (Enter key triggers OK, Esc key triggers Cancel)
            form.AcceptButton = buttonOk;
            form.CancelButton = buttonCancel;

            // 7. Show the Dialog and wait for result
            DialogResult result = form.ShowDialog();

            // 8. Return data
            if (result == DialogResult.OK)
            {
                return textBox.Text;
            }

            return null;
        }
    }
}

#endif