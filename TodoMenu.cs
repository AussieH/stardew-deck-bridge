using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewDeckBridge;

internal sealed class TodoMenu : IClickableMenu, IKeyboardSubscriber
{
    private const int RowHeight = 60;
    private const int Pad = 44;

    private static readonly Rectangle SlotBox = new(384, 373, 18, 18);
    private static readonly Rectangle ButtonBox = new(432, 439, 9, 9);
    private static readonly Rectangle RemoveIcon = new(337, 494, 12, 12);
    private static readonly Rectangle UpIcon = new(421, 459, 11, 12);
    private static readonly Rectangle DownIcon = new(421, 472, 11, 12);

    private string input = "";
    private int? editingId;
    private int scroll;
    private int visibleRows;
    private string status = "";
    private int statusTicks;
    private int hoverX, hoverY;

    private Rectangle inputBox, addButton, upArrow, downArrow, clearButton, list;

    public bool Selected { get; set; }

    public TodoMenu()
    {
        this.Layout();
        Game1.keyboardDispatcher.Subscriber = this;
        this.exitFunction = this.Unsubscribe;
    }

    private void Layout()
    {
        int w = Math.Min(1100, Game1.uiViewport.Width - 64);
        int h = Math.Min(820, Game1.uiViewport.Height - 64);
        this.initialize((Game1.uiViewport.Width - w) / 2, (Game1.uiViewport.Height - h) / 2, w, h, true);
        int x = this.xPositionOnScreen, y = this.yPositionOnScreen;

        this.inputBox = new Rectangle(x + Pad, y + 112, w - Pad * 2 - 80, 64);
        this.addButton = new Rectangle(this.inputBox.Right + 16, this.inputBox.Y, 64, 64);
        this.clearButton = new Rectangle(x + Pad, y + h - Pad - 60, 240, 60);
        int top = this.inputBox.Bottom + 24, bottom = this.clearButton.Y - 16;
        this.list = new Rectangle(x + Pad, top, w - Pad * 2 - 60, bottom - top);
        this.visibleRows = Math.Max(1, this.list.Height / RowHeight);
        this.upArrow = new Rectangle(this.list.Right + 16, top, 44, 48);
        this.downArrow = new Rectangle(this.list.Right + 16, top + this.visibleRows * RowHeight - 48, 44, 48);
        this.ClampScroll();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => this.Layout();

    public void Unsubscribe()
    {
        if (Game1.keyboardDispatcher.Subscriber == this) Game1.keyboardDispatcher.Subscriber = null;
        this.Selected = false;
    }

    public static void ReleaseIfOrphaned()
    {
        if (Game1.keyboardDispatcher?.Subscriber is TodoMenu menu && Game1.activeClickableMenu != menu) menu.Unsubscribe();
    }

    protected override void cleanupBeforeExit()
    {
        this.Unsubscribe();
        base.cleanupBeforeExit();
    }

    public override void emergencyShutDown()
    {
        this.Unsubscribe();
        base.emergencyShutDown();
    }

    private IEnumerable<(TodoItem Item, Rectangle Row, Rectangle Box, Rectangle Text, Rectangle Remove)> Rows()
    {
        var items = Todos.Items;
        for (int i = this.scroll; i < items.Count && i < this.scroll + this.visibleRows; i++)
        {
            int rowY = this.list.Y + (i - this.scroll) * RowHeight;
            var row = new Rectangle(this.list.X, rowY, this.list.Width, RowHeight);
            var box = new Rectangle(row.X + 8, rowY + 12, 36, 36);
            var remove = new Rectangle(row.Right - 44, rowY + 12, 36, 36);
            var text = new Rectangle(box.Right + 16, rowY, remove.X - box.Right - 32, RowHeight);
            yield return (items[i], row, box, text, remove);
        }
    }

    private void ClampScroll() => this.scroll = Math.Clamp(this.scroll, 0, Math.Max(0, Todos.Items.Count - this.visibleRows));

    private void Say(string text, string sound)
    {
        this.status = text;
        this.statusTicks = 150;
        if (sound.Length > 0) Game1.playSound(sound);
    }

    private void Commit()
    {
        string text = this.input.Trim();
        if (this.editingId is int id)
        {
            if (text.Length == 0) { this.CancelEdit(); return; }
            string? editError = Todos.Edit(id, text);
            if (editError is not null) { this.Say(editError, "cancel"); return; }
            this.editingId = null;
            this.input = "";
            this.Say("Saved", "coin");
            return;
        }
        if (text.Length == 0) return;
        string? error = Todos.Add(text);
        if (error is not null) { this.Say(error, "cancel"); return; }
        this.input = "";
        this.scroll = Todos.Items.Count;
        this.ClampScroll();
        this.Say("Added", "newRecipe");
    }

    private void CancelEdit()
    {
        this.editingId = null;
        this.input = "";
        Game1.playSound("bigDeSelect");
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (Game1.activeClickableMenu != this) return;

        if (this.addButton.Contains(x, y)) { this.Commit(); return; }
        if (this.inputBox.Contains(x, y)) { Game1.keyboardDispatcher.Subscriber = this; return; }
        if (this.upArrow.Contains(x, y) && this.scroll > 0) { this.scroll--; Game1.playSound("shwip"); return; }
        if (this.downArrow.Contains(x, y) && Todos.Items.Count > this.visibleRows) { this.scroll++; this.ClampScroll(); Game1.playSound("shwip"); return; }
        if (this.clearButton.Contains(x, y))
        {
            string? error = Todos.ClearDone();
            if (error is null) { this.ClampScroll(); this.Say("Cleared the ticked to-dos", "trashcan"); }
            else this.Say(error, "cancel");
            return;
        }

        foreach (var r in this.Rows())
        {
            if (r.Box.Contains(x, y))
            {
                Todos.Toggle(r.Item.Id);
                Game1.playSound(r.Item.Done ? "coin" : "drumkit6");
                return;
            }
            if (r.Remove.Contains(x, y))
            {
                if (this.editingId == r.Item.Id) { this.editingId = null; this.input = ""; }
                Todos.Remove(r.Item.Id);
                this.ClampScroll();
                Game1.playSound("trashcan");
                return;
            }
            if (r.Text.Contains(x, y))
            {
                this.editingId = r.Item.Id;
                this.input = r.Item.Text;
                Game1.keyboardDispatcher.Subscriber = this;
                Game1.playSound("smallSelect");
                return;
            }
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.scroll -= Math.Sign(direction);
        this.ClampScroll();
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key != Keys.Escape) return;
        if (this.editingId is not null) this.CancelEdit();
        else this.exitThisMenu();
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button == Buttons.B) { if (this.editingId is not null) this.CancelEdit(); else this.exitThisMenu(); }
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.hoverX = x;
        this.hoverY = y;
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (Game1.activeClickableMenu == this && Game1.keyboardDispatcher.Subscriber is null) Game1.keyboardDispatcher.Subscriber = this;
        if (this.statusTicks > 0) this.statusTicks--;
    }

    public void RecieveTextInput(char inputChar)
    {
        if (char.IsControl(inputChar) || this.input.Length >= Todos.MaxLength) return;
        this.input += inputChar;
    }

    public void RecieveTextInput(string text)
    {
        foreach (char c in text ?? "") this.RecieveTextInput(c);
    }

    public void RecieveCommandInput(char command)
    {
        if (command == '\b' && this.input.Length > 0) this.input = this.input[..^1];
        else if (command == '\r') this.Commit();
    }

    public void RecieveSpecialInput(Keys key) { }

    private static string Fit(string text, SpriteFont font, int width)
    {
        if (font.MeasureString(text).X <= width) return text;
        while (text.Length > 0 && font.MeasureString(text + "...").X > width) text = text[..^1];
        return text.TrimEnd() + "...";
    }

    private static void Text(SpriteBatch b, string text, SpriteFont font, float x, float y, Color color) =>
        Utility.drawTextWithShadow(b, text, font, new Vector2(x, y), color, 1f, -1f, -1, -1, 1f, 3);

    public override void draw(SpriteBatch b)
    {
        var font = Game1.smallFont;
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);
        drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

        const string title = "To-do list";
        var titleSize = Game1.dialogueFont.MeasureString(title);
        Text(b, title, Game1.dialogueFont, this.xPositionOnScreen + (this.width - titleSize.X) / 2, this.yPositionOnScreen + 40, Game1.textColor);

        drawTextureBox(b, Game1.mouseCursors, SlotBox, this.inputBox.X, this.inputBox.Y, this.inputBox.Width, this.inputBox.Height, Color.White, 4f, false, -1f);
        int room = this.inputBox.Width - 40;
        float textY = this.inputBox.Y + (this.inputBox.Height - font.MeasureString("Ag").Y) / 2 + 2;
        if (this.input.Length == 0)
        {
            string placeholder = this.editingId is null ? "Type a new to-do and press Enter" : "Type the new text";
            Text(b, Fit(placeholder, font, room), font, this.inputBox.X + 20, textY, Game1.textColor * 0.45f);
        }
        string shown = this.input;
        while (shown.Length > 0 && font.MeasureString(shown).X > room) shown = shown[1..];
        if (shown.Length > 0) Text(b, shown, font, this.inputBox.X + 20, textY, Game1.textColor);
        if (DateTime.UtcNow.Millisecond < 500)
            b.Draw(Game1.staminaRect, new Rectangle(this.inputBox.X + 22 + (int)font.MeasureString(shown).X, (int)textY, 4, 32), Game1.textColor);

        float okAlpha = this.input.Trim().Length > 0 ? 1f : 0.45f;
        b.Draw(Game1.mouseCursors, new Vector2(this.addButton.X, this.addButton.Y), Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
            Color.White * okAlpha, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

        var items = Todos.Items;
        if (items.Count == 0)
        {
            const string empty = "Nothing on the list yet.";
            Text(b, empty, font, this.list.X + (this.list.Width - font.MeasureString(empty).X) / 2, this.list.Y + 40, Game1.textColor * 0.6f);
        }
        foreach (var r in this.Rows())
        {
            bool editing = this.editingId == r.Item.Id;
            if (editing) b.Draw(Game1.staminaRect, r.Row, Color.Gold * 0.35f);
            else if (r.Row.Contains(this.hoverX, this.hoverY)) b.Draw(Game1.staminaRect, r.Row, Color.Wheat * 0.35f);

            b.Draw(Game1.mouseCursors, new Vector2(r.Box.X, r.Box.Y), r.Item.Done ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked,
                Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);

            string line = Fit(r.Item.Text, font, r.Text.Width);
            var size = font.MeasureString(line);
            float y = r.Row.Y + (RowHeight - size.Y) / 2 + 2;
            Color color = r.Item.Done ? Game1.textColor * 0.5f : Game1.textColor;
            Text(b, line, font, r.Text.X, y, color);
            if (r.Item.Done) b.Draw(Game1.staminaRect, new Rectangle(r.Text.X, (int)(y + size.Y / 2), (int)size.X, 3), color);

            float removeAlpha = r.Remove.Contains(this.hoverX, this.hoverY) ? 1f : 0.7f;
            b.Draw(Game1.mouseCursors, new Vector2(r.Remove.X, r.Remove.Y), RemoveIcon, Color.White * removeAlpha, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.9f);
        }
        if (items.Count > this.visibleRows)
        {
            b.Draw(Game1.mouseCursors, new Vector2(this.upArrow.X, this.upArrow.Y), UpIcon, Color.White * (this.scroll > 0 ? 1f : 0.35f), 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
            bool more = this.scroll < items.Count - this.visibleRows;
            b.Draw(Game1.mouseCursors, new Vector2(this.downArrow.X, this.downArrow.Y), DownIcon, Color.White * (more ? 1f : 0.35f), 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
        }

        bool anyDone = items.Any(i => i.Done);
        drawTextureBox(b, Game1.mouseCursors, ButtonBox, this.clearButton.X, this.clearButton.Y, this.clearButton.Width, this.clearButton.Height,
            Color.White * (anyDone ? 1f : 0.5f), 4f, false, -1f);
        const string clear = "Clear ticked";
        var clearSize = font.MeasureString(clear);
        Text(b, clear, font, this.clearButton.X + (this.clearButton.Width - clearSize.X) / 2, this.clearButton.Y + (this.clearButton.Height - clearSize.Y) / 2 + 2,
            Game1.textColor * (anyDone ? 1f : 0.5f));

        string hint = this.statusTicks > 0 ? this.status
            : this.editingId is not null ? "Enter saves  ·  Esc cancels"
            : $"{items.Count(i => !i.Done)} to do  ·  click a to-do to change it  ·  Esc closes";
        int hintRoom = this.xPositionOnScreen + this.width - Pad - this.clearButton.Right - 24;
        string hintLine = Fit(hint, font, hintRoom);
        Text(b, hintLine, font, this.xPositionOnScreen + this.width - Pad - font.MeasureString(hintLine).X, this.clearButton.Y + (this.clearButton.Height - clearSize.Y) / 2 + 2,
            Game1.textColor * 0.75f);

        base.draw(b);
        this.drawMouse(b);
    }
}
