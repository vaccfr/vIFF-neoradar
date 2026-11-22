using System;
using System.Collections.Generic;
using NeoRadar.PluginSdk.Logger;
using NeoRadar.PluginSdk.Tag;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Tags;

/// <summary>
/// Registers the tag actions, builds their dropdowns and routes clicks to <see cref="FlightActions"/>.
/// </summary>
internal sealed class TagMenus
{
    private const string ReadyTobt = "ready-tobt";
    private const string EditTobt = "edit-tobt";
    private const string SendRea = "rea-on";
    private const string CancelRea = "rea-off";
    private const string SetManualCtot = "manual-ctot";
    private const string RemoveManualCtot = "manual-ctot-off";
    private const string DeIcePrefix = "deice:";

    private readonly ITagInterface _tags;
    private readonly CdmStore _store;
    private readonly AirportControls _controls;
    private readonly FlightActions _actions;
    private readonly ILoggerApi _logger;

    private string _tobtMenu = string.Empty;
    private string _tobtReady = string.Empty;
    private string _tobtEdit = string.Empty;
    private string _readyToggle = string.Empty;
    private string _readyWithRea = string.Empty;
    private string _ttotMenu = string.Empty;
    private string _ctotMenu = string.Empty;
    private string _etfmsMenu = string.Empty;
    private string _deIceMenu = string.Empty;

    public TagMenus(ITagInterface tags, CdmStore store, AirportControls controls, FlightActions actions, ILoggerApi logger)
    {
        _tags = tags;
        _store = store;
        _controls = controls;
        _actions = actions;
        _logger = logger;
    }

    public void Register()
    {
        _tobtMenu = Action("TOBT Menu", false, "E/TOBT menu: Ready TOBT (sends REA with a CTOT) and Edit TOBT");
        _tobtReady = Action("TOBT Ready", false, "Sets the TOBT to now, sending REA when the flight has a CTOT");
        _tobtEdit = Action("TOBT Edit", true, "Asks for a TOBT, HHMM UTC; empty clears it");
        _readyToggle = Action("RDY", false, "Sets or clears RDY (the ASRT)");
        _readyWithRea = Action("RDY REA", false, "Sets RDY and sends a Ready (REA) message");
        _ttotMenu = Action("TTOT Menu", false, "TTOT menu: hold the take-off to a time");
        _ctotMenu = Action("CTOT Menu", false, "CTOT menu: request a revised CTOT, REA, manual CTOT");
        _etfmsMenu = Action("ETFMS Menu", false, "Network status menu: send or cancel REA");
        _deIceMenu = Action("DEICE Menu", false, "De-icing menu: none, on stand or a remote pad");
    }

    // Client threads.
    public void OnTagAction(TagActionEvent evt)
    {
        string callsign = CallsignOf(evt.PlanCallsign, evt.Callsign);
        if (evt.ActionId == _tobtReady)
        {
            _actions.ReadyTobt(callsign);
        }
        else if (evt.ActionId == _tobtEdit)
        {
            _actions.EditTobt(callsign, evt.UserInput);
        }
        else if (evt.ActionId == _readyToggle)
        {
            _actions.ToggleReady(callsign, withRea: false);
        }
        else if (evt.ActionId == _readyWithRea)
        {
            _actions.ToggleReady(callsign, withRea: true);
        }
    }

    public bool OnTagShowDropdown(TagShowDropdownEvent evt)
    {
        string callsign = CallsignOf(evt.PlanCallsign, evt.Callsign);
        DepartureRecord? record = _store.Find(callsign);
        DropdownDefinition? dropdown = record is null ? null : Build(evt.ActionId, record);
        if (dropdown is null)
        {
            return false;
        }

        if (_tags.GetDropdownForAction(evt.ActionId) is null)
        {
            _tags.SetActionDropdown(evt.ActionId, dropdown);
        }
        else
        {
            _tags.UpdateActionDropdown(evt.ActionId, dropdown);
        }

        return true;
    }

    public void OnTagDropdownAction(DropdownActionEvent evt)
    {
        string callsign = CallsignOf(evt.PlanCallsign, evt.Callsign);
        switch (evt.ComponentId)
        {
            case ReadyTobt:
                _actions.ReadyTobt(callsign);
                break;
            case EditTobt:
                _actions.EditTobt(callsign, evt.UserInput);
                break;
            case SendRea:
                _actions.SetRea(callsign, ready: true);
                break;
            case CancelRea:
                _actions.SetRea(callsign, ready: false);
                break;
            case SetManualCtot:
                _actions.SetManualCtot(callsign, evt.UserInput);
                break;
            case RemoveManualCtot:
                _actions.SetManualCtot(callsign, string.Empty);
                break;
            default:
                if (evt.ComponentId.StartsWith(DeIcePrefix, StringComparison.Ordinal))
                {
                    _actions.SetDeIce(callsign, evt.ComponentId[DeIcePrefix.Length..]);
                }

                break;
        }
    }

    private DropdownDefinition? Build(string actionId, DepartureRecord record)
    {
        bool isCdm = _store.Airport(record.Departure).IsCdm;
        string ctot = CdmTime.Display(record.EffectiveCtot);
        bool rea = record.IsRea || string.Equals(record.CdmStatus, "REA", StringComparison.OrdinalIgnoreCase);
        string word = isCdm ? "TOBT" : "EOBT";

        if (actionId == _tobtMenu)
        {
            return Menu(record.Callsign,
                Button(ReadyTobt, ctot.Length > 0 ? $"Ready {word} + REA" : $"Ready {word}"),
                Input(EditTobt, $"Edit {word} (HHMM)"));
        }

        if (actionId == _ttotMenu)
        {
            return Menu(record.Callsign,
                Label($"TTOT {CdmTime.Display(record.Cdm.Ttot)}"),
                Input(SetManualCtot, "Hold take-off to (HHMM)"),
                Button(RemoveManualCtot, "Release take-off hold"));
        }

        if (actionId == _ctotMenu)
        {
            List<DropdownComponent> components = new();
            if (ctot.Length > 0)
            {
                components.Add(Label($"CTOT {ctot} {(record.HasManualCtot ? CdmDataUpdate.ManualReason : record.Regulation)}"));
            }

            components.Add(Input(EditTobt, $"New {word} for revised CTOT (HHMM)"));
            components.Add(Button(rea ? CancelRea : SendRea, rea ? "Cancel REA" : "Send REA"));
            components.Add(new DropdownComponent { Type = DropdownComponentType.Divider });
            components.Add(Input(SetManualCtot, "Set manual CTOT (HHMM)"));
            if (record.HasManualCtot)
            {
                components.Add(Button(RemoveManualCtot, "Remove manual CTOT"));
            }

            return Menu(record.Callsign, components.ToArray());
        }

        if (actionId == _etfmsMenu)
        {
            return Menu(record.Callsign,
                Label($"Status {CdmTagFormatter.Etfms(record)}"),
                Button(rea ? CancelRea : SendRea, rea ? "Cancel REA" : "Send REA"));
        }

        if (actionId == _deIceMenu)
        {
            string current = DeIce.Label(record.Cdm.Deice);
            List<DropdownComponent> components = new()
            {
                Radio(DeIcePrefix, "NONE", current.Length == 0),
                Radio(DeIcePrefix + DeIce.StandLabel, DeIce.StandLabel, current == DeIce.StandLabel),
            };
            foreach ((string name, int extra) in _controls.DeIcePads(record.Departure))
            {
                components.Add(Radio(DeIcePrefix + name, extra > 0 ? $"{name} (+{extra} min)" : name, current == name));
            }

            return Menu(record.Callsign, components.ToArray());
        }

        return null;
    }

    private string Action(string name, bool requiresInput, string description)
    {
        string id = _tags.RegisterTagAction(new TagActionDefinition(name, requiresInput, description));
        if (id.Length == 0)
        {
            _logger.Error($"The tag action {name} was refused");
        }

        return id;
    }

    private static string CallsignOf(string planCallsign, string callsign) => planCallsign.Length > 0 ? planCallsign : callsign;

    private static DropdownDefinition Menu(string title, params DropdownComponent[] components) => new(title, 200, 320, components);

    private static DropdownComponent Button(string id, string text) => new() { Id = id, Type = DropdownComponentType.Button, Text = text };

    private static DropdownComponent Input(string id, string text) =>
        new() { Id = id, Type = DropdownComponentType.Button, Text = text, RequiresInput = true };

    private static DropdownComponent Label(string text) => new() { Type = DropdownComponentType.Label, Text = text };

    private static DropdownComponent Radio(string id, string text, bool selected) =>
        new() { Id = id, Type = DropdownComponentType.Radio, Text = text, Checked = selected };
}
